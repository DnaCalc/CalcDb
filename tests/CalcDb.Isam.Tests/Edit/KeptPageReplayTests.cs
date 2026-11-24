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
            Workspace = new TransactionCoordinator(new LockerId(1)) { Publisher = this, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
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

        Assert.Equal(MutationOpen.None, s.Workspace.PublishCloseCommit(s.Cache).Open);
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

        Assert.Equal(MutationOpen.None, s.Workspace.PublishCloseCommit(s.Cache).Open);
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
            using var s = new WAccdbSession(File);
            work(s);
            s.CloseRecordsets();
            s.Ws.EndLazyBurst(File.Cache);
            if (s.Ws.NeedsCloseCommit(File.Cache))
            {
                Assert.Equal(MutationOpen.None, s.Ws.PublishCloseCommit(File.Cache).Open);
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

        public void Dispose() => File.Dispose();
    }

    private sealed class WAccdbSession(ReplayFixture file) : IDisposable
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
            using var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = mdb, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
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

    private static byte[]? KeptSlice(string card, string question, string handleName, long byteOffset, int expectedLength, string attempt = "a1")
    {
        string folder = Path.Combine(ReplayFixture.Root(), "evidence", card, question);
        using JsonDocument doc = JsonDocument.Parse(System.IO.File.ReadAllText(Directory.GetFiles(folder, $"*.{attempt}.json").Single()));
        byte[]? found = null;
        Walk(doc.RootElement, handleName, byteOffset, expectedLength, ref found);
        return found;

        static void Walk(JsonElement e, string handleName, long byteOffset, int expectedLength, ref byte[]? found)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                if (e.TryGetProperty("handle", out JsonElement handle) && handle.GetString() == handleName
                    && e.TryGetProperty("bytes", out JsonElement bytes)
                    && e.TryGetProperty("offset", out JsonElement offset) && offset.GetInt64() == byteOffset
                    && e.TryGetProperty("bytesRead", out JsonElement read) && read.GetInt32() == expectedLength)
                {
                    found = Convert.FromHexString(bytes.GetString()!);
                }
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    Walk(p.Value, handleName, byteOffset, expectedLength, ref found);
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement x in e.EnumerateArray())
                {
                    Walk(x, handleName, byteOffset, expectedLength, ref found);
                }
            }
        }
    }

    private static byte[] SeedGrowthOpen(string question, string card = "r-q19-growth-open")
    {
        SortedDictionary<long, byte[]> created = KeptPages(card, question, "pg-after-create");
        byte[] image = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(EmptyAccdb);
        long maxPage = created.Keys.Max();
        if ((maxPage + 1) * D.PageSize > image.Length)
        {
            Array.Resize(ref image, (int)((maxPage + 1) * D.PageSize));
        }
        foreach ((long p, byte[] bytes) in created)
        {
            bytes.CopyTo(image, (int)(p * D.PageSize));
        }
        if (!created.ContainsKey(1))
        {
            foreach (long p in created.Keys)
            {
                if (p >= 42)
                {
                    Assert.True(MapPages.TryInlineBit(image.AsSpan(D.PageSize, D.PageSize), D.Map.GlobalFreeSlot, (uint)p, D, out int offset, out byte mask, out string? bitFailure), bitFailure);
                    image[D.PageSize + offset] &= (byte)~mask;
                }
            }
        }
        if (KeptSlice(card, question, "pg-after-create", 3584, 8) is { } headerSlice)
        {
            headerSlice.CopyTo(image, 3584);
        }
        return image;
    }

    private static void CloseLvSession(Session s, byte[]? _ = null)
    {
        if (s.Workspace.NeedsCloseCommit(s.Cache))
        {
            (MutationOpen open, string? detail) = s.Workspace.PublishCloseCommit(s.Cache);
            Assert.Equal(MutationOpen.None, open);
            Assert.Null(detail);
        }
    }

    private static void AssertMatchesGrowthOpen(string question, string handleName, byte[] ours, string card = "r-q19-growth-open")
    {
        SortedDictionary<long, byte[]> after = KeptPages(card, question, handleName);
        List<string> differences = [];
        foreach ((long page, byte[] theirs) in after)
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
        if (KeptSlice(card, question, handleName, 3584, 8) is { } expectedHeader)
        {
            for (int i = 0; i < expectedHeader.Length; i++)
            {
                if (ours[3584 + i] != expectedHeader[i])
                {
                    differences.Add($"page 0 +{3584 + i}: ours {ours[3584 + i]:X2}, ACE {expectedHeader[i]:X2}");
                }
            }
        }
        Assert.True(differences.Count == 0, $"{card}/{question} ({handleName}): {differences.Count} differences:\n" + string.Join("\n", differences.Take(40)));
    }

    private static unsafe NativeVariant MakeLvVariant(bool memo, int byteLength, out nint ownedBstr, char fill = '€')
    {
        if (memo)
        {
            ownedBstr = Bstrs.FromUnits(byteLength / 2, byteLength / 2, (span, n) => span[..n].Fill(fill));
            return new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = ownedBstr };
        }
        ownedBstr = 0;
        nint psa = SafeArrays.Create(VarType.UI1, (0, byteLength));
        if (byteLength > 0)
        {
            byte* data = (byte*)SafeArrays.ElementPointer(psa, 0);
            for (int b = 0; b < byteLength; b++)
            {
                data[b] = (byte)((b * 7) + 3);
            }
        }
        return new NativeVariant { Vt = (ushort)(VarType.Array | VarType.UI1), Pointer = psa };
    }

    /// <summary>
    /// R-FMT-2 "LV form selection" (evidence/r-q19-growth-open/lv-inline-memo and lv-inline-ole):
    /// Memo (2, 50, 64, 66, 98 bytes) and Long Binary (1, 50, 64, 65, 99 bytes), one value per shared session.
    /// Up to 64 bytes stay inline in the data row (n | 0x80000000, 8 zero bytes, payload); from 65 bytes a single
    /// LVAL row lands on a new LVAL page (46, in LV owned and LV free maps). Every kept page (1, 42–53) and page 0
    /// commit counter match ACE byte-for-byte after every session.
    /// </summary>
    [Theory]
    [InlineData("lv-inline-memo", true, new[] { 2, 50, 64, 66, 98 })]
    [InlineData("lv-inline-ole", false, new[] { 1, 50, 64, 65, 99 })]
    public void Long_values_inline_bound_matches_ACE_byte_for_byte(string question, bool memo, int[] sizes)
    {
        byte[] image = SeedGrowthOpen(question);
        for (int i = 0; i < sizes.Length; i++)
        {
            byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            NativeVariant value = MakeLvVariant(memo, sizes[i], out nint ownedBstr);
            try
            {
                s.Insert(t, () => 0u, ("ID", I4(i + 1)), ("V", value));
            }
            finally
            {
                if (ownedBstr != 0)
                {
                    Bstrs.Free(ownedBstr);
                }
            }
            CloseLvSession(s, page1Before);
            image = s.File.Snapshot();
            AssertMatchesGrowthOpen(question, $"pg-after-insert-{i + 1}", image);
        }
    }

    /// <summary>
    /// R-FMT-2 "LV form selection" (evidence/r-q19-growth-open/lv-single-*):
    /// Up to 3,816 bytes store as a single LVAL row (length | 0x40000000, stamp 0, page joins LV free map even with
    /// 264 bytes free at 3,816). From 3,817 bytes the value uses the chained form (no flag, tick stamp in +8 and page
    /// bytes 8–11, not in LV free map): 3,817..4,072 take one chain page (4,072 leaves 4 bytes free), 4,074 takes two
    /// (4,072 + 2). Every kept page (1, 42–53) and page 0 commit counter match ACE byte-for-byte.
    /// </summary>
    [Theory]
    [InlineData("lv-single-2002", 2002)]
    [InlineData("lv-single-3816", 3816)]
    [InlineData("lv-single-3817", 3817)]
    [InlineData("lv-single-4072", 4072)]
    [InlineData("lv-single-4074", 4074)]
    public void Long_values_single_row_and_chain_boundary_matches_ACE_byte_for_byte(string question, int size)
    {
        byte[] image = SeedGrowthOpen(question);
        SortedDictionary<long, byte[]> after = KeptPages("r-q19-growth-open", question, "pg-after-insert-1");
        uint stamp = size > LongValuePlan.SingleRowPayloadMax ? BinaryPrimitives.ReadUInt32LittleEndian(after[45].AsSpan(8)) : 0u;
        byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        NativeVariant value = MakeLvVariant(memo: false, size, out _);
        s.Insert(t, () => stamp, ("ID", I4(1)), ("V", value));
        CloseLvSession(s, page1Before);
        AssertMatchesGrowthOpen(question, "pg-after-insert-1", s.File.Snapshot());
    }

    /// <summary>
    /// R-FMT-2 "LV form selection and Map ownership" (evidence/r-q19-growth-open/lv-fill-2000-1976, -1978, -2000):
    /// 100 and 2,000 bytes leave LVAL page 45 with 1,978 free. Then 1,976 (+2 slot = 1,978) fills page 45 to 0 free
    /// bytes and clears 45 from the LV free map, so the next 100 bytes take new page 47 (all 4 sessions match ACE
    /// byte-for-byte). 1,978 and 2,000 do not fit with their slot, leave 45 in the LV free map, and take new page 47
    /// (sessions 1–3 match ACE byte-for-byte); session 4 (100B) then sees two LV free-map pages with room ({45, 47})
    /// and is refused by name because r-q22-lv-open/lv-two-free-lower-looser-* disproved both first-fit and best-fit.
    /// </summary>
    [Theory]
    [InlineData("lv-fill-2000-1976", 1976, true)]
    [InlineData("lv-fill-2000-1978", 1978, false)]
    [InlineData("lv-fill-2000-2000", 2000, false)]
    public void Long_values_LVAL_fill_and_free_map_matches_ACE_byte_for_byte(string question, int thirdSize, bool fourthAdmitted)
    {
        int[] sizes = [100, 2000, thirdSize, 100];
        byte[] image = SeedGrowthOpen(question);
        for (int i = 0; i < sizes.Length; i++)
        {
            byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            if (i == 3 && !fourthAdmitted)
            {
                using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
                var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
                using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
                using var frame = new EditFrame(ctx, cur, s.Workspace);
                Assert.True(frame.AddNew().Succeeded);
                Assert.True(frame.Assign(t.Columns[0], I4(4)).Succeeded);
                Assert.True(frame.Assign(t.Columns[1], MakeLvVariant(memo: false, sizes[i], out _)).Succeeded);
                UpdateOutcome refused = frame.Update();
                Assert.False(refused.Succeeded);
                Assert.Equal(MutationOpen.Publication, refused.Open);
                Assert.Contains("2 long-value free-map pages have room", refused.Detail);
                return;
            }
            NativeVariant value = MakeLvVariant(memo: false, sizes[i], out _);
            s.Insert(t, () => 0u, ("ID", I4(i + 1)), ("V", value));
            CloseLvSession(s, page1Before);
            image = s.File.Snapshot();
            AssertMatchesGrowthOpen(question, $"pg-after-insert-{i + 1}", image);
        }
    }

    /// <summary>
    /// R-FMT-2 "LV form selection and Overflow / deletion" (all 10 evidence/r-q19-growth-open/lv-op-* records):
    /// replacing a stored single Memo (larger, smaller, crossing to chain, clearing to Null), replacing a stored
    /// chained Memo (larger chain, smaller chain, shrinking to single, clearing to Null), and deleting a row holding
    /// a single or chained Memo. Checks every kept page (1, 42–57) and page 0 commit counter (+3586: +2 when UPDATE
    /// allocates chain pages or frees a chain and reserves/returns its 4-byte page-1 extent, +1 for single-row
    /// UPDATE/Null and for BOTH DELETEs including lv-op-delete-row-chain) byte-for-byte, plus reads back surviving
    /// rows through <see cref="FieldDecoder"/>.
    /// </summary>
    [Theory]
    [InlineData("lv-op-single-larger", 1, 200, false)]
    [InlineData("lv-op-single-smaller", 1, 80, false)]
    [InlineData("lv-op-single-to-chain", 1, 5000, false)]
    [InlineData("lv-op-single-null", 1, 0, false)]
    [InlineData("lv-op-chain-larger", 3, 6000, false)]
    [InlineData("lv-op-chain-smaller", 3, 4500, false)]
    [InlineData("lv-op-chain-to-single", 3, 100, false)]
    [InlineData("lv-op-chain-null", 3, 0, false)]
    [InlineData("lv-op-delete-row-single", 1, -1, true)]
    [InlineData("lv-op-delete-row-chain", 3, -1, true)]
    public void Long_values_replace_clear_and_delete_match_ACE_byte_for_byte(string question, int targetId, int newBytes, bool isDelete)
    {
        byte[] image = SeedGrowthOpen(question);
        SortedDictionary<long, byte[]> after = KeptPages("r-q19-growth-open", question, "pg-after-op");
        uint stamp = !isDelete && newBytes > LongValuePlan.SingleRowPayloadMax
            ? BinaryPrimitives.ReadUInt32LittleEndian(after[49].AsSpan(8))
            : 0u;
        byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
        var context = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount) { LongValueTicks = () => stamp });
        using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
        using var frame = new EditFrame(context, cursor, s.Workspace);
        ColumnDescriptor idCol = t.Columns.Single(c => c.Name == "ID");
        ColumnDescriptor vCol = t.Columns.Single(c => c.Name == "V");
        bool found = false;
        for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
        {
            if (cursor.Read(idCol).Value.Integer == targetId)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, $"row ID={targetId} not found");

        using (StatementScope stmt = s.Workspace.BeginStatement())
        {
            if (isDelete)
            {
                UpdateOutcome del = frame.Delete();
                Assert.True(del.Succeeded && del.Change?.Physical is not null, $"{question}: {del} / {del.Change?.NotWritten}");
            }
            else
            {
                Assert.True(frame.Edit().Succeeded);
                nint ownedBstr = 0;
                NativeVariant val = newBytes == 0
                    ? new NativeVariant { Vt = (ushort)VarType.Null }
                    : MakeLvVariant(memo: true, newBytes, out ownedBstr);
                try
                {
                    Assert.True(frame.Assign(vCol, val).Succeeded);
                    UpdateOutcome upd = frame.Update();
                    Assert.True(upd.Succeeded && upd.Change?.Physical is not null, $"{question}: {upd} / {upd.Change?.NotWritten}");
                }
                finally
                {
                    if (ownedBstr != 0)
                    {
                        Bstrs.Free(ownedBstr);
                    }
                }
            }
            ResolutionOutcome res = stmt.Complete();
            Assert.Equal(MutationOpen.None, res.Open);
        }

        CloseLvSession(s, page1Before);
        byte[] finalImage = s.File.Snapshot();
        AssertMatchesGrowthOpen(question, "pg-after-op", finalImage);

        // Verify round-trip read of every surviving row.
        using var verify = new Session(finalImage);
        TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using TableCursor readCur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
        Dictionary<long, int?> readSizes = [];
        for (readCur.MoveFirst(); !readCur.Eof; readCur.MoveNext())
        {
            long id = readCur.Read(tAfter.Columns[0]).Value.Integer;
            NativeValue v = readCur.Read(tAfter.Columns[1]).Value;
            Assert.NotEqual(NativeKind.Failed, v.Kind);
            readSizes[id] = v.Kind == NativeKind.Null ? null : v.Text!.Length * 2;
        }
        if (isDelete)
        {
            Assert.False(readSizes.ContainsKey(targetId));
            Assert.Equal(2, readSizes.Count);
        }
        else
        {
            Assert.Equal(3, readSizes.Count);
            Assert.Equal(newBytes == 0 ? null : newBytes, readSizes[targetId]);
        }
    }

    /// <summary>
    /// R-FMT-2 "LV form selection and Overflow / deletion" (evidence/r-q22-lv-open/lv-free-timing-delete-chain):
    /// DELETE frees chained LVAL pages 47 and 48 in page 1 (4037..4038: 00 fe -> 80 ff) and clears them in the LV
    /// owned map during the statement, with page 0 +3586 = +1 and no Close write. Verifies byte-for-byte identity at
    /// BOTH <c>pg-open-after-op</c> (before Close) and <c>pg-after-close</c>.
    /// </summary>
    [Fact]
    public void Long_values_delete_chain_frees_page1_during_statement_matches_ACE_byte_for_byte()
    {
        const string card = "r-q22-lv-open";
        const string question = "lv-free-timing-delete-chain";
        byte[] image = SeedGrowthOpen(question, card);
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
        var context = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
        using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
        using var frame = new EditFrame(context, cursor, s.Workspace);
        for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(t.Columns[0]).Value.Integer != 3; cursor.MoveNext())
        {
        }
        Assert.False(cursor.Eof);
        using (StatementScope stmt = s.Workspace.BeginStatement())
        {
            UpdateOutcome del = frame.Delete();
            Assert.True(del.Succeeded && del.Change?.Physical is not null, $"{del} / {del.Change?.NotWritten}");
            Assert.Equal(MutationOpen.None, stmt.Complete().Open);
        }

        // Before Close: page 1 already marks 47 and 48 free and page 0 +3586 is +1.
        AssertMatchesGrowthOpen(question, "pg-open-after-op", s.File.Snapshot(), card);
        Assert.False(s.Workspace.ReleasesAtClose(s.Cache));
        Assert.False(s.Workspace.NeedsCloseCommit(s.Cache));

        // Close writes nothing.
        CloseLvSession(s);
        AssertMatchesGrowthOpen(question, "pg-after-close", s.File.Snapshot(), card);
    }

    /// <summary>
    /// R-FMT-2 "LV form selection and Overflow / deletion" (evidence/r-q22-lv-open/lv-free-timing-update-chain-smaller
    /// and lv-free-timing-update-chain-to-single): UPDATE frees chained LVAL pages 47 and 48 in page 1 and the LV
    /// owned map during the statement, reserves the unused tail of the 4-byte page-1 extent (51..79 when allocating
    /// 49 &amp; 50; 50..79 leaving 49 free when placing on existing page 45), and returns those reserved bits at Close
    /// (+1 on page 0 +3586, total +2). Verifies byte-for-byte identity at BOTH <c>pg-open-after-op</c> and
    /// <c>pg-after-close</c>, plus <see cref="FieldDecoder"/> readback.
    /// </summary>
    [Theory]
    [InlineData("lv-free-timing-update-chain-smaller", 4500)]
    [InlineData("lv-free-timing-update-chain-to-single", 100)]
    public void Long_values_update_chain_frees_page1_and_reserves_extent_matches_ACE_byte_for_byte(string question, int newBytes)
    {
        const string card = "r-q22-lv-open";
        byte[] image = SeedGrowthOpen(question, card);
        SortedDictionary<long, byte[]> openAfter = KeptPages(card, question, "pg-open-after-op");
        uint stamp = newBytes > LongValuePlan.SingleRowPayloadMax
            ? BinaryPrimitives.ReadUInt32LittleEndian(openAfter[49].AsSpan(8))
            : 0u;
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
        var context = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount) { LongValueTicks = () => stamp });
        using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
        using var frame = new EditFrame(context, cursor, s.Workspace);
        for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(t.Columns[0]).Value.Integer != 3; cursor.MoveNext())
        {
        }
        Assert.False(cursor.Eof);
        using (StatementScope stmt = s.Workspace.BeginStatement())
        {
            Assert.True(frame.Edit().Succeeded);
            NativeVariant val = MakeLvVariant(memo: true, newBytes, out nint ownedBstr);
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                UpdateOutcome upd = frame.Update();
                Assert.True(upd.Succeeded && upd.Change?.Physical is not null, $"{question}: {upd} / {upd.Change?.NotWritten}");
            }
            finally
            {
                if (ownedBstr != 0)
                {
                    Bstrs.Free(ownedBstr);
                }
            }
            Assert.Equal(MutationOpen.None, stmt.Complete().Open);
        }

        // Before Close: old chain pages 47 and 48 are free in page 1, unused 4-byte extent bits are reserved (0),
        // and page 0 +3586 is +1.
        AssertMatchesGrowthOpen(question, "pg-open-after-op", s.File.Snapshot(), card);
        Assert.True(s.Workspace.ReleasesAtClose(s.Cache));
        Assert.True(s.Workspace.NeedsCloseCommit(s.Cache));

        // Close returns the reserved bits to 1 and increments page 0 +3586 to +2.
        CloseLvSession(s);
        Assert.False(s.Workspace.ReleasesAtClose(s.Cache));
        Assert.False(s.Workspace.NeedsCloseCommit(s.Cache));
        byte[] finalImage = s.File.Snapshot();
        AssertMatchesGrowthOpen(question, "pg-after-close", finalImage, card);

        using var verify = new Session(finalImage);
        TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using TableCursor readCur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
        for (readCur.MoveFirst(); !readCur.Eof && readCur.Read(tAfter.Columns[0]).Value.Integer != 3; readCur.MoveNext())
        {
        }
        Assert.False(readCur.Eof);
        NativeValue read = readCur.Read(tAfter.Columns[1]).Value;
        Assert.Equal(NativeKind.Text, read.Kind);
        Assert.Equal(newBytes, read.Text!.Length * 2);
    }

    /// <summary>
    /// R-FMT-2 "LV form selection" (evidence/r-q22-lv-open/lv-bound-room2000-2500, lv-bound-room2000-3900,
    /// lv-bound-room3980-3900, lv-memo-bound-3816, lv-memo-bound-3818):
    /// The 3,816/3,817 single-row-to-chain bound holds regardless of whether the column already has an LVAL page with
    /// room (1,978B or 3,980B free) and for plain uncompressed Memo as well as OLE. Every kept page and page 0 commit
    /// counter match ACE byte-for-byte after every session.
    /// </summary>
    [Theory]
    [InlineData("lv-bound-room2000-2500", false, new[] { 100, 2000, 2500 }, -1, 0)]
    [InlineData("lv-bound-room2000-3900", false, new[] { 100, 2000, 3900 }, 2, 47)]
    [InlineData("lv-bound-room3980-3900", false, new[] { 100, 3900 }, 1, 47)]
    [InlineData("lv-memo-bound-3816", true, new[] { 3816 }, -1, 0)]
    [InlineData("lv-memo-bound-3818", true, new[] { 3818 }, 0, 45)]
    public void Long_values_3816_bound_holds_with_existing_LVAL_room_and_plain_Memo_matches_ACE_byte_for_byte(
        string question, bool memo, int[] sizes, int chainedInsertIndex, long stampPage)
    {
        const string card = "r-q22-lv-open";
        byte[] image = SeedGrowthOpen(question, card);
        for (int i = 0; i < sizes.Length; i++)
        {
            uint stamp = 0u;
            if (i == chainedInsertIndex)
            {
                SortedDictionary<long, byte[]> expected = KeptPages(card, question, $"pg-after-insert-{i + 1}");
                stamp = BinaryPrimitives.ReadUInt32LittleEndian(expected[stampPage].AsSpan(8));
            }
            byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            NativeVariant value = MakeLvVariant(memo, sizes[i], out nint ownedBstr);
            try
            {
                s.Insert(t, () => stamp, ("ID", I4(i + 1)), ("V", value));
            }
            finally
            {
                if (ownedBstr != 0)
                {
                    Bstrs.Free(ownedBstr);
                }
            }
            CloseLvSession(s, page1Before);
            image = s.File.Snapshot();
            AssertMatchesGrowthOpen(question, $"pg-after-insert-{i + 1}", image, card);
        }
    }

    /// <summary>
    /// R-FMT-2 "LV form selection" (evidence/r-q22-lv-open/lv-chain-3-pages, lv-chain-4-pages):
    /// 10,000-byte (3 chain pages 45–47 + data page 48) and 14,000-byte (4 chain pages 45–48 + data page 49) chains
    /// link forward in ascending page order with the tick stamp on page 45 only and 0 on continuation pages. Every
    /// kept page and page 0 commit counter match ACE byte-for-byte, and <see cref="FieldDecoder"/> reads back the exact
    /// patterned bytes.
    /// </summary>
    [Theory]
    [InlineData("lv-chain-3-pages", 10000)]
    [InlineData("lv-chain-4-pages", 14000)]
    public void Long_values_3_and_4_page_chains_match_ACE_byte_for_byte(string question, int byteLength)
    {
        const string card = "r-q22-lv-open";
        byte[] image = SeedGrowthOpen(question, card);
        SortedDictionary<long, byte[]> after = KeptPages(card, question, "pg-after-insert-1");
        uint stamp = BinaryPrimitives.ReadUInt32LittleEndian(after[45].AsSpan(8));
        byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        s.Insert(t, () => stamp, ("ID", I4(1)), ("V", MakeLvVariant(memo: false, byteLength, out _)));
        CloseLvSession(s, page1Before);
        byte[] finalImage = s.File.Snapshot();
        AssertMatchesGrowthOpen(question, "pg-after-insert-1", finalImage, card);

        using var verify = new Session(finalImage);
        TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using TableCursor cur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
        cur.MoveFirst();
        Assert.False(cur.Eof);
        NativeValue read = cur.Read(tAfter.Columns[1]).Value;
        Assert.Equal(NativeKind.LongBinary, read.Kind);
        Assert.Equal(byteLength, read.Bytes!.Length);
        for (int b = 0; b < byteLength; b++)
        {
            Assert.Equal((byte)((b * 7) + 3), read.Bytes[b]);
        }
    }

    /// <summary>
    /// R-FMT-2 "LV form selection" (evidence/r-q22-lv-open/lv-inline-tight-3958, lv-inline-tight-3960,
    /// lv-inline-two-memos-60):
    /// A &lt;= 64-byte Memo stays inline up to a 4,060-byte data row (lv-inline-tight-3958: 20B free on page 45, out of
    /// FreeMap) and spills to a single LVAL row (page 45, before data page 46) when inlining would exceed 4,060 bytes
    /// (lv-inline-tight-3960: 4,002B data row, 78B free, out of FreeMap); the 64-byte bound is per column
    /// (lv-inline-two-memos-60: two 60-byte Memos both inline in a 159B row). Every kept page matches ACE byte-for-byte.
    /// </summary>
    [Fact]
    public void Long_values_inline_4060_spill_and_per_column_bound_match_ACE_byte_for_byte()
    {
        const string card = "r-q22-lv-open";

        foreach ((string question, int t8Chars) in new[] { ("lv-inline-tight-3958", 194), ("lv-inline-tight-3960", 195) })
        {
            byte[] image = SeedGrowthOpen(question, card);
            byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            List<nint> bstrs = [];
            try
            {
                List<(string Column, NativeVariant Value)> row = [("ID", I4(1))];
                for (int k = 1; k <= 7; k++)
                {
                    NativeVariant v = MakeLvVariant(memo: true, 255 * 2, out nint b);
                    bstrs.Add(b);
                    row.Add(($"T{k}", v));
                }
                {
                    NativeVariant v8 = MakeLvVariant(memo: true, t8Chars * 2, out nint b8);
                    bstrs.Add(b8);
                    row.Add(("T8", v8));
                    NativeVariant vm = MakeLvVariant(memo: true, 30 * 2, out nint bm);
                    bstrs.Add(bm);
                    row.Add(("M", vm));
                }
                s.Insert(t, () => 0u, [.. row]);
            }
            finally
            {
                foreach (nint b in bstrs)
                {
                    Bstrs.Free(b);
                }
            }
            CloseLvSession(s, page1Before);
            byte[] finalImage = s.File.Snapshot();
            AssertMatchesGrowthOpen(question, "pg-after-insert-1", finalImage, card);

            using var verify = new Session(finalImage);
            TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using TableCursor cur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
            cur.MoveFirst();
            Assert.False(cur.Eof);
            NativeValue mRead = cur.Read(tAfter.Columns.Single(c => c.Name == "M")).Value;
            Assert.Equal(NativeKind.Text, mRead.Kind);
            Assert.Equal(30, mRead.Text!.Length);
        }

        // lv-inline-two-memos-60: two 60-byte Memos (sum 120 > 64) both inline on page 45.
        {
            const string question = "lv-inline-two-memos-60";
            byte[] image = SeedGrowthOpen(question, card);
            byte[] page1Before = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            NativeVariant m1 = MakeLvVariant(memo: true, 60, out nint b1);
            NativeVariant m2 = MakeLvVariant(memo: true, 60, out nint b2);
            try
            {
                s.Insert(t, () => 0u, ("ID", I4(1)), ("M1", m1), ("M2", m2));
            }
            finally
            {
                Bstrs.Free(b1);
                Bstrs.Free(b2);
            }
            CloseLvSession(s, page1Before);
            byte[] finalImage = s.File.Snapshot();
            AssertMatchesGrowthOpen(question, "pg-after-insert-1", finalImage, card);

            using var verify = new Session(finalImage);
            TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using TableCursor cur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
            cur.MoveFirst();
            Assert.False(cur.Eof);
            Assert.Equal(30, cur.Read(tAfter.Columns.Single(c => c.Name == "M1")).Value.Text!.Length);
            Assert.Equal(30, cur.Read(tAfter.Columns.Single(c => c.Name == "M2")).Value.Text!.Length);
        }
    }

    /// <summary>
    /// Named refusals directly pinned against the open r-q22-lv-open and r-q19-growth-open cases:
    /// (1) a second chain-freeing UPDATE in one session while a prior page-1 reservation is active (not recorded),
    /// (1b) a chain-freeing UPDATE replacing a chain by a single-row value that allocates a new LVAL page (not recorded),
    /// (1c) a chain-freeing UPDATE whose new chain allocates past <c>firstFreeBefore</c>'s global-map byte (not recorded;
    /// also verifies <see cref="PageCache.KnownExtentPages"/> is not raised by the refused compilation so a subsequent
    /// valid UPDATE in the same session still succeeds),
    /// (4a/4b/4c) a chain-freeing UPDATE paired with another allocation in the same session (prior allocation, subsequent
    /// allocation while a reservation is pending, or earlier row in one multi-row UPDATE),
    /// (5a/5b) 0-allocation extent reservation at <c>firstFreeBefore % 8 == 0</c> or <c>7</c>,
    /// (6a/6b/6c/6d) chain -&gt; inline (&lt;= 64B) on UPDATE, Long Binary (OLE) chain removal on UPDATE/DELETE,
    /// chain-freeing UPDATE inside <c>BeginTrans</c>, and multi-LV-column UPDATE freeing a chain,
    /// (2) MEMO WITH COMPRESSION (lv-memo-comp-100, -1908, -1909),
    /// (3) two LV free-map pages with room (lv-two-free-lower-looser-100, -250, -400, -1400, -1500, -same-session:
    /// inserts 1–3 replay byte-for-byte, insert 4 refuses by name).
    /// </summary>
    [Fact]
    public void Long_values_r_q22_open_cases_refuse_by_name()
    {
        // 1. A second chain-freeing UPDATE in the same session while the first UPDATE's page-1 reservation is still
        // active: refused by name rather than guessing how ACE reuses or extends an active reservation.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-smaller", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant first = MakeLvVariant(memo: true, 4500, out nint owned1);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], first).Succeeded);
                    UpdateOutcome ok = frame.Update();
                    Assert.True(ok.Succeeded && ok.Change?.Physical is not null, $"{ok} / {ok.Change?.NotWritten}");
                }
                finally
                {
                    Bstrs.Free(owned1);
                }
                Assert.Equal(MutationOpen.None, stmt.Complete().Open);
            }
            // Second SQL UPDATE in the same open session freeing the new chain while pages 47..48 are free inside
            // the extent (and pages 51..79 are reserved in page 1): refused by name.
            using (StatementScope stmt2 = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant second = MakeLvVariant(memo: true, 100, out nint owned2);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], second).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "second chain-freeing UPDATE in same open session should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("earlier allocation, free, or page-1 reservation", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned2);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt2.Complete();
            }
        }

        // 1b. SQL UPDATE replacing a chain by a single-row value that does not fit on existing LV free page 45
        // (1,978B free) and therefore allocates a new LVAL page: refused by name.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-to-single", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 2500, out nint owned);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chain -> new single-row LVAL page should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("allocates a new LVAL page", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
        }

        // 1c. SQL UPDATE freeing a chain whose new chain allocates past firstFreeBefore's global-map byte
        // (firstFreeBefore = 49 in byte 6 [48..55]; a 32,000B payload allocates 8 chain pages 49..56 so
        // maxAllocated = 56 lies in byte 7 != byte 6): refused by name without modifying the file or raising
        // PageCache.KnownExtentPages, so a subsequent valid UPDATE in the same session still succeeds.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-smaller", "r-q22-lv-open");
            using var s = new Session(image);
            byte[] before = s.File.Snapshot();
            ulong extentBefore = s.File.PageCount;
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 32_000, out nint owned);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chain allocation spilling into next global-map byte should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("spills past first free page", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
            Assert.Equal(before, s.File.Snapshot());
            Assert.True(s.Cache.KnownExtentPages <= extentBefore, $"refused compilation must not raise KnownExtentPages ({s.Cache.KnownExtentPages} > {extentBefore})");

            // A valid 100B chain-freeing UPDATE in the same session still succeeds and keeps the extent unchanged.
            using (StatementScope stmt2 = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant valid = MakeLvVariant(memo: true, 100, out nint owned2);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], valid).Succeeded);
                    UpdateOutcome ok = frame.Update();
                    Assert.True(ok.Succeeded && ok.Change?.Physical is not null, $"{ok} / {ok.Change?.NotWritten}");
                }
                finally
                {
                    Bstrs.Free(owned2);
                }
                Assert.Equal(MutationOpen.None, stmt2.Complete().Open);
            }
            Assert.True(s.Cache.KnownExtentPages <= extentBefore);
        }

        // 4a. Prior allocating INSERT followed by a chain-freeing SQL UPDATE in the same session: refused by name.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-smaller", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            NativeVariant v4 = MakeLvVariant(memo: true, 5000, out nint owned4);
            try
            {
                s.Insert(t, () => 0u, ("ID", I4(4)), ("V", v4));
            }
            finally
            {
                Bstrs.Free(owned4);
            }
            byte[] before = s.File.Snapshot();
            TableDefinition tNow = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", tNow, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, tNow, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, tNow, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, tNow, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(tNow.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 100, out nint owned);
                try
                {
                    Assert.True(frame.Assign(tNow.Columns[1], val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chain-freeing UPDATE after earlier allocation should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("earlier allocation, free, or page-1 reservation", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
            Assert.Equal(before, s.File.Snapshot());
        }

        // 4b. Chain-freeing SQL UPDATE followed by an allocating INSERT in the same session while the reservation
        // is pending: refused by name without modifying the file.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-to-single", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 100, out nint owned);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                    UpdateOutcome ok = frame.Update();
                    Assert.True(ok.Succeeded && ok.Change?.Physical is not null, $"{ok} / {ok.Change?.NotWritten}");
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.Equal(MutationOpen.None, stmt.Complete().Open);
            }
            byte[] before = s.File.Snapshot();
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(t.Columns[0], I4(4)).Succeeded);
            NativeVariant allocVal = MakeLvVariant(memo: true, 5000, out nint ownedAlloc);
            try
            {
                Assert.True(frame.Assign(t.Columns[1], allocVal).Succeeded);
                UpdateOutcome refused = frame.Update();
                Assert.False(refused.Succeeded, "allocating INSERT while page-1 extent reservation is pending should refuse");
                Assert.Equal(MutationOpen.Publication, refused.Open);
                Assert.Contains("page-1 extent reservation is pending", refused.Detail);
            }
            finally
            {
                Bstrs.Free(ownedAlloc);
            }
            Assert.True(frame.CancelUpdate().Succeeded);
            Assert.Equal(before, s.File.Snapshot());
        }

        // 4c. Multi-row SQL UPDATE in one statement where row 1 allocates pages and row 2 frees a chain: row 2 refuses by name.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-smaller", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 1; cur.MoveNext())
                {
                }
                Assert.False(cur.Eof);
                Assert.True(frame.Edit().Succeeded);
                NativeVariant row1Val = MakeLvVariant(memo: true, 5000, out nint owned1);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], row1Val).Succeeded);
                    UpdateOutcome ok1 = frame.Update();
                    Assert.True(ok1.Succeeded && ok1.Change?.Physical is not null, $"{ok1} / {ok1.Change?.NotWritten}");
                }
                finally
                {
                    Bstrs.Free(owned1);
                }

                for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
                {
                }
                Assert.False(cur.Eof);
                Assert.True(frame.Edit().Succeeded);
                NativeVariant row2Val = MakeLvVariant(memo: true, 100, out nint owned2);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], row2Val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "row 2 freeing a chain after row 1 allocated in same UPDATE should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("earlier allocation, free, or page-1 reservation", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned2);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                Assert.Equal(MutationOpen.None, stmt.Complete().Open);
            }
        }

        // 5a & 5b. 0-allocation extent reservation at firstFreeBefore % 8 == 0 or 7: refused by name.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-to-single", "r-q22-lv-open");
            byte[] page1 = image.AsSpan(D.PageSize, D.PageSize).ToArray();
            using var s = new Session(image);
            var allocator = new PageAllocator(s.Cache, D, s.File.PageCount);

            Assert.False(allocator.TryReserveExtent(48, page1, [], [], out string? err48));
            Assert.Contains("starts a global-map byte", err48);

            Assert.False(allocator.TryReserveExtent(55, page1, [], [], out string? err55));
            Assert.Contains("last bit of its global-map byte", err55);
        }

        // 6a. Chain -> inline (<= 64B) on SQL UPDATE: refused by name without modifying the file.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-to-single", "r-q22-lv-open");
            using var s = new Session(image);
            byte[] before = s.File.Snapshot();
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 20, out nint owned);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chain -> inline (<= 64B) on UPDATE should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("by an inline", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
            Assert.Equal(before, s.File.Snapshot());
        }

        // 6b. Long Binary (OLE) chain removal on SQL UPDATE and DELETE: refused by name without modifying the file.
        {
            byte[] seed = SeedGrowthOpen("lv-chain-3-pages", "r-q22-lv-open");
            byte[] p1 = seed.AsSpan(D.PageSize, D.PageSize).ToArray();
            byte[] oleImg;
            using (var setup = new Session(seed))
            {
                TableDefinition tSetup = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => setup.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                setup.Insert(tSetup, () => 0u, ("ID", I4(1)), ("V", MakeLvVariant(memo: false, 5000, out _)));
                CloseLvSession(setup, p1);
                oleImg = setup.File.Snapshot();
            }

            using (var sUpd = new Session(oleImg))
            {
                TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => sUpd.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
                var ctx = new TableEditContext(semantics, new StoredUniqueProbe(sUpd.Cache, D, t, sUpd.File.PageCount), 7177, false, new TableFootprints(sUpd.Cache, D, t, sUpd.File.PageCount));
                using TableCursor cur = TableCursor.Open(sUpd.Cache, D, t, sUpd.File.PageCount, 0, out _)!;
                using var frame = new EditFrame(ctx, cur, sUpd.Workspace);
                cur.MoveFirst();
                Assert.False(cur.Eof);
                using (StatementScope stmt = sUpd.Workspace.BeginStatement())
                {
                    Assert.True(frame.Edit().Succeeded);
                    Assert.True(frame.Assign(t.Columns[1], new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chained OLE UPDATE should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("chained Long Binary (OLE)", refused.Detail);
                    Assert.True(frame.CancelUpdate().Succeeded);
                    stmt.Complete();
                }
                Assert.Equal(oleImg, sUpd.File.Snapshot());
            }

            using (var sDel = new Session(oleImg))
            {
                TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => sDel.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
                var ctx = new TableEditContext(semantics, new StoredUniqueProbe(sDel.Cache, D, t, sDel.File.PageCount), 7177, false, new TableFootprints(sDel.Cache, D, t, sDel.File.PageCount));
                using TableCursor cur = TableCursor.Open(sDel.Cache, D, t, sDel.File.PageCount, 0, out _)!;
                using var frame = new EditFrame(ctx, cur, sDel.Workspace);
                cur.MoveFirst();
                Assert.False(cur.Eof);
                UpdateOutcome refused = frame.Delete();
                Assert.False(refused.Succeeded, "chained OLE DELETE should refuse");
                Assert.Equal(MutationOpen.Publication, refused.Open);
                Assert.Contains("chained Long Binary (OLE)", refused.Detail);
                Assert.Equal(oleImg, sDel.File.Snapshot());
            }
        }

        // 6c. Chain-freeing SQL UPDATE inside explicit BeginTrans: refused by name without modifying the file.
        {
            byte[] image = SeedGrowthOpen("lv-free-timing-update-chain-to-single", "r-q22-lv-open");
            using var s = new Session(image);
            byte[] before = s.File.Snapshot();
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != 3; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            Assert.Null(s.Workspace.BeginTrans());
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                NativeVariant val = MakeLvVariant(memo: true, 100, out nint owned);
                try
                {
                    Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "chain-freeing UPDATE inside BeginTrans should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("explicit transaction (BeginTrans)", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(owned);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
            Assert.True(s.Workspace.Rollback().Succeeded);
            Assert.Equal(before, s.File.Snapshot());
        }

        // 6d. Multi-LV-column SQL UPDATE freeing a chain: refused by name without modifying the file.
        {
            byte[] seed = SeedGrowthOpen("lv-inline-two-memos-60", "r-q22-lv-open");
            byte[] p1 = seed.AsSpan(D.PageSize, D.PageSize).ToArray();
            byte[] twoMemoImg;
            using (var setup = new Session(seed))
            {
                TableDefinition tSetup = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => setup.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                NativeVariant m1 = MakeLvVariant(memo: true, 5000, out nint b1);
                try
                {
                    setup.Insert(tSetup, () => 0u, ("ID", I4(1)), ("M1", m1), ("M2", new NativeVariant { Vt = (ushort)VarType.Null }));
                }
                finally
                {
                    Bstrs.Free(b1);
                }
                CloseLvSession(setup, p1);
                twoMemoImg = setup.File.Snapshot();
            }

            using var s = new Session(twoMemoImg);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            ColumnDescriptor m1Col = t.Columns.Single(c => c.Name == "M1");
            ColumnDescriptor m2Col = t.Columns.Single(c => c.Name == "M2");
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            cur.MoveFirst();
            Assert.False(cur.Eof);
            using (StatementScope stmt = s.Workspace.BeginStatement())
            {
                Assert.True(frame.Edit().Succeeded);
                Assert.True(frame.Assign(m1Col, new NativeVariant { Vt = (ushort)VarType.Null }).Succeeded);
                NativeVariant m2Val = MakeLvVariant(memo: true, 100, out nint b2);
                try
                {
                    Assert.True(frame.Assign(m2Col, m2Val).Succeeded);
                    UpdateOutcome refused = frame.Update();
                    Assert.False(refused.Succeeded, "multi-LV-column UPDATE freeing a chain should refuse");
                    Assert.Equal(MutationOpen.Publication, refused.Open);
                    Assert.Contains("multiple long-value columns", refused.Detail);
                }
                finally
                {
                    Bstrs.Free(b2);
                }
                Assert.True(frame.CancelUpdate().Succeeded);
                stmt.Complete();
            }
            Assert.Equal(twoMemoImg, s.File.Snapshot());
        }

        // 2. MEMO WITH COMPRESSION (ExtendedFlags = 0x01): refused by name via TextCodec.TryEncode.
        foreach ((string question, int chars) in new[]
        {
            ("lv-memo-comp-100", 100),
            ("lv-memo-comp-1908", 1908),
            ("lv-memo-comp-1909", 1909),
        })
        {
            byte[] image = SeedGrowthOpen(question, "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            Assert.NotEqual(0, t.Columns[1].ExtendedFlags & 0x01);
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(t.Columns[0], I4(1)).Succeeded);
            NativeVariant val = MakeLvVariant(memo: true, chars * 2, out nint owned, fill: 'a');
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                UpdateOutcome refused = frame.Update();
                Assert.False(refused.Succeeded, $"{question} should refuse");
                Assert.Equal(MutationOpen.Publication, refused.Open);
                Assert.Contains("compress", refused.Detail, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Bstrs.Free(owned);
            }
        }

        // 3. Two LV free-map pages with room (45: 1,978B free, 47: 1,580B free): inserts 1–3 replay byte-for-byte
        // against ACE, and insert 4 (100, 250, 400, 1400, 1500B, across fresh sessions and in one session) refuses by name.
        foreach ((string question, int fourthSize) in new[]
        {
            ("lv-two-free-lower-looser-100", 100),
            ("lv-two-free-lower-looser-250", 250),
            ("lv-two-free-lower-looser-400", 400),
            ("lv-two-free-lower-looser-1400", 1400),
            ("lv-two-free-lower-looser", 1500),
        })
        {
            byte[] image = SeedGrowthOpen(question, "r-q22-lv-open");
            int[] setup = [100, 2000, 2500];
            for (int i = 0; i < setup.Length; i++)
            {
                byte[] p1 = image.AsSpan(D.PageSize, D.PageSize).ToArray();
                using var s = new Session(image);
                TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                s.Insert(t, () => 0u, ("ID", I4(i + 1)), ("V", MakeLvVariant(memo: false, setup[i], out _)));
                CloseLvSession(s, p1);
                image = s.File.Snapshot();
                AssertMatchesGrowthOpen(question, $"pg-after-insert-{i + 1}", image, "r-q22-lv-open");
            }
            using var s4 = new Session(image);
            TableDefinition t4 = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s4.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var sem4 = MutationSemantics.Compile("V", t4, null, new NoRules(), D.IndexDescriptor);
            var ctx4 = new TableEditContext(sem4, new StoredUniqueProbe(s4.Cache, D, t4, s4.File.PageCount), 7177, false, new TableFootprints(s4.Cache, D, t4, s4.File.PageCount));
            using TableCursor cur4 = TableCursor.Open(s4.Cache, D, t4, s4.File.PageCount, 0, out _)!;
            using var frame4 = new EditFrame(ctx4, cur4, s4.Workspace);
            Assert.True(frame4.AddNew().Succeeded);
            Assert.True(frame4.Assign(t4.Columns[0], I4(4)).Succeeded);
            Assert.True(frame4.Assign(t4.Columns[1], MakeLvVariant(memo: false, fourthSize, out _)).Succeeded);
            UpdateOutcome refused = frame4.Update();
            Assert.False(refused.Succeeded, $"{question} insert 4 should refuse");
            Assert.Equal(MutationOpen.Publication, refused.Open);
            Assert.Contains("2 long-value free-map pages have room", refused.Detail);
        }

        // Same-session control (lv-two-free-lower-looser-same-session): 100, 2000, 2500 succeed in one session, 1500 refuses.
        {
            byte[] image = SeedGrowthOpen("lv-two-free-lower-looser-same-session", "r-q22-lv-open");
            using var s = new Session(image);
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            s.Insert(t, () => 0u, ("ID", I4(1)), ("V", MakeLvVariant(memo: false, 100, out _)));
            s.Insert(t, () => 0u, ("ID", I4(2)), ("V", MakeLvVariant(memo: false, 2000, out _)));
            s.Insert(t, () => 0u, ("ID", I4(3)), ("V", MakeLvVariant(memo: false, 2500, out _)));

            TableDefinition tNow = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var sem = MutationSemantics.Compile("V", tNow, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(sem, new StoredUniqueProbe(s.Cache, D, tNow, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, tNow, s.File.PageCount));
            using TableCursor cur = TableCursor.Open(s.Cache, D, tNow, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(tNow.Columns[0], I4(4)).Succeeded);
            Assert.True(frame.Assign(tNow.Columns[1], MakeLvVariant(memo: false, 1500, out _)).Succeeded);
            UpdateOutcome refused = frame.Update();
            Assert.False(refused.Succeeded);
            Assert.Equal(MutationOpen.Publication, refused.Open);
            Assert.Contains("2 long-value free-map pages have room", refused.Detail);
        }
    }

    /// <summary>
    /// Inline long values (1..64 bytes, R-FMT-2 "LV form selection"): replacing an inline Memo with a shorter inline
    /// Memo, promoting an inline Memo to a single LVAL row, clearing an inline Memo to Null, and deleting a row
    /// holding an inline Memo. Verifies every byte decided by the recorded row/page/slot/map rules
    /// (<c>lv-inline-memo</c>, <c>lv-op-single-null</c>, <c>slot-delete-middle-after-shrink</c>, <c>slot-delete-bottom</c>)
    /// as well as <see cref="FieldDecoder"/> read-back.
    /// </summary>
    [Fact]
    public void Long_values_inline_replace_clear_and_delete_round_trip()
    {
        byte[] image = SeedGrowthOpen("lv-inline-memo");
        using var s = new Session(image);
        TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
        var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
        LvalColumnMapSpec lvMaps = t.LongValueMaps.Single();

        void InsertMemo(int id, int byteLen)
        {
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            using StatementScope stmt = s.Workspace.BeginStatement();
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(t.Columns[0], I4(id)).Succeeded);
            NativeVariant val = MakeLvVariant(memo: true, byteLen, out nint owned);
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                UpdateOutcome ok = frame.Update();
                Assert.True(ok.Succeeded && ok.Change?.Physical is not null, $"{ok} / {ok.Change?.NotWritten}");
            }
            finally
            {
                if (owned != 0)
                {
                    Bstrs.Free(owned);
                }
            }
            Assert.Equal(MutationOpen.None, stmt.Complete().Open);
        }

        void UpdateMemo(int id, int? byteLen)
        {
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != id; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using StatementScope stmt = s.Workspace.BeginStatement();
            Assert.True(frame.Edit().Succeeded);
            nint owned = 0;
            NativeVariant val = byteLen is { } len ? MakeLvVariant(memo: true, len, out owned) : new NativeVariant { Vt = (ushort)VarType.Null };
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                UpdateOutcome ok = frame.Update();
                Assert.True(ok.Succeeded && ok.Change?.Physical is not null, $"{ok} / {ok.Change?.NotWritten}");
            }
            finally
            {
                if (owned != 0)
                {
                    Bstrs.Free(owned);
                }
            }
            Assert.Equal(MutationOpen.None, stmt.Complete().Open);
        }

        void DeleteRow(int id)
        {
            using TableCursor cur = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cur, s.Workspace);
            for (cur.MoveFirst(); !cur.Eof && cur.Read(t.Columns[0]).Value.Integer != id; cur.MoveNext())
            {
            }
            Assert.False(cur.Eof);
            using StatementScope stmt = s.Workspace.BeginStatement();
            UpdateOutcome del = frame.Delete();
            Assert.True(del.Succeeded && del.Change?.Physical is not null, $"{del} / {del.Change?.NotWritten}");
            Assert.Equal(MutationOpen.None, stmt.Complete().Open);
        }

        byte[] ReadPage(uint page) => s.File.Snapshot().AsSpan((int)(page * D.PageSize), D.PageSize).ToArray();

        static byte[] ReadVFieldBytes(byte[] dataPage, int slotIndex, TableDefinition td)
        {
            SlotView slot = DataPages.CheckedSlot(dataPage, slotIndex, D, out string? err)!.Value;
            Assert.Null(err);
            ReadOnlySpan<byte> body = dataPage.AsSpan(slot.Start, slot.Length);
            Assert.True(RowEnvelope.TryOpen(body, D, out RowEnvelope env, out string? envErr), envErr);
            FieldRead f = env.ReadField(td.Columns[1], td.KnownFixedEnd(env.ColumnCount, D));
            Assert.Equal(FieldOutcome.Bytes, f.Outcome);
            return body[f.Start..f.End].ToArray();
        }

        // 1. Insert three inline Memo rows (64B, 40B, 20B): data page 45 holds 89B, 65B, 45B; LV maps stay empty.
        InsertMemo(1, 64);
        InsertMemo(2, 40);
        InsertMemo(3, 20);
        {
            byte[] p45 = ReadPage(45);
            Assert.Equal(3u, D.DataPage.RowCount.Read(p45));
            Assert.Equal(3877u, D.DataPage.FreeCount.Read(p45));
            SlotView s0 = DataPages.CheckedSlot(p45, 0, D, out _)!.Value;
            SlotView s1 = DataPages.CheckedSlot(p45, 1, D, out _)!.Value;
            SlotView s2 = DataPages.CheckedSlot(p45, 2, D, out _)!.Value;
            Assert.Equal((4007, 89), (s0.Start, s0.Length));
            Assert.Equal((3942, 65), (s1.Start, s1.Length));
            Assert.Equal((3897, 45), (s2.Start, s2.Length));
            Assert.Equal(new byte[] { 0x40, 0x00, 0x00, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, ReadVFieldBytes(p45, 0, t)[..12]);
            Assert.Equal((byte)0x03, p45[s0.End - 1]);
            Assert.Empty(UsageMaps.Pages(s.Cache, D, lvMaps.OwnedMap, s.File.PageCount, out _)!);
            Assert.Empty(UsageMaps.Pages(s.Cache, D, lvMaps.FreeMap, s.File.PageCount, out _)!);
        }

        // 2. Inline 64B -> inline 20B: slot 0 shrinks 89B -> 45B, slots 1 & 2 shift up by 44B, FreeCount +44, no LVAL page.
        UpdateMemo(1, 20);
        {
            byte[] p45 = ReadPage(45);
            Assert.Equal(3921u, D.DataPage.FreeCount.Read(p45));
            SlotView s0 = DataPages.CheckedSlot(p45, 0, D, out _)!.Value;
            SlotView s1 = DataPages.CheckedSlot(p45, 1, D, out _)!.Value;
            SlotView s2 = DataPages.CheckedSlot(p45, 2, D, out _)!.Value;
            Assert.Equal((4051, 45), (s0.Start, s0.Length));
            Assert.Equal((3986, 65), (s1.Start, s1.Length));
            Assert.Equal((3941, 45), (s2.Start, s2.Length));
            Assert.Equal(new byte[] { 0x14, 0x00, 0x00, 0x80, 0, 0, 0, 0, 0, 0, 0, 0 }, ReadVFieldBytes(p45, 0, t)[..12]);
            Assert.Empty(UsageMaps.Pages(s.Cache, D, lvMaps.OwnedMap, s.File.PageCount, out _)!);
        }

        // 3. Inline 20B -> single LVAL row 100B: allocates LVAL page 46 (in LV owned & free maps, stamp 0),
        // slot 0 shrinks 45B -> 25B with single-row ref [64 00 00 40 00 2E 00 00 00 00 00 00], slots 1 & 2 shift up by 20B.
        UpdateMemo(1, 100);
        byte[] lval46AfterPromote;
        {
            byte[] p45 = ReadPage(45);
            Assert.Equal(3941u, D.DataPage.FreeCount.Read(p45));
            SlotView s0 = DataPages.CheckedSlot(p45, 0, D, out _)!.Value;
            SlotView s1 = DataPages.CheckedSlot(p45, 1, D, out _)!.Value;
            SlotView s2 = DataPages.CheckedSlot(p45, 2, D, out _)!.Value;
            Assert.Equal((4071, 25), (s0.Start, s0.Length));
            Assert.Equal((4006, 65), (s1.Start, s1.Length));
            Assert.Equal((3961, 45), (s2.Start, s2.Length));
            Assert.Equal(new byte[] { 0x64, 0x00, 0x00, 0x40, 0x00, 0x2E, 0x00, 0x00, 0, 0, 0, 0 }, ReadVFieldBytes(p45, 0, t));
            Assert.Equal([46u], UsageMaps.Pages(s.Cache, D, lvMaps.OwnedMap, s.File.PageCount, out _)!);
            Assert.Equal([46u], UsageMaps.Pages(s.Cache, D, lvMaps.FreeMap, s.File.PageCount, out _)!);

            lval46AfterPromote = ReadPage(46);
            Assert.True(DataPages.IsLvalPage(lval46AfterPromote, D));
            Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(lval46AfterPromote.AsSpan(8)));
            Assert.Equal(1u, D.DataPage.RowCount.Read(lval46AfterPromote));
            Assert.Equal(3980u, D.DataPage.FreeCount.Read(lval46AfterPromote));
            SlotView lv0 = DataPages.CheckedSlot(lval46AfterPromote, 0, D, out _)!.Value;
            Assert.Equal((3996, 100), (lv0.Start, lv0.Length));
        }

        // 4. Inline 40B -> Null: slot 1 shrinks 65B -> 13B with null-mask byte 0x01 (lv-op-single-null), slot 2 shifts up by 52B.
        UpdateMemo(2, null);
        {
            byte[] p45 = ReadPage(45);
            Assert.Equal(3993u, D.DataPage.FreeCount.Read(p45));
            SlotView s1 = DataPages.CheckedSlot(p45, 1, D, out _)!.Value;
            SlotView s2 = DataPages.CheckedSlot(p45, 2, D, out _)!.Value;
            Assert.Equal((4058, 13), (s1.Start, s1.Length));
            Assert.Equal((byte)0x01, p45[s1.End - 1]);
            Assert.Equal((4013, 45), (s2.Start, s2.Length));
            Assert.Equal(lval46AfterPromote, ReadPage(46));
        }

        // 5. Delete row 3 (inline 20B): slot 2 word becomes 0xC000 | 4058 (slot-delete-bottom), FreeCount +45, RowCount 3 -> 2.
        DeleteRow(3);
        {
            byte[] p45 = ReadPage(45);
            Assert.Equal(4038u, D.DataPage.FreeCount.Read(p45));
            ushort deletedSlotWord = BinaryPrimitives.ReadUInt16LittleEndian(p45.AsSpan(D.DataPage.SlotBase + (2 * D.DataPage.SlotEntryBytes)));
            Assert.Equal((ushort)(0xC000 | 4058), deletedSlotWord);
            Assert.Equal(lval46AfterPromote, ReadPage(46));
        }

        using var verify = new Session(s.File.Snapshot());
        TableDefinition tAfter = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => verify.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
        Assert.Equal(2u, tAfter.RowCount);
        using TableCursor readCur = TableCursor.Open(verify.Cache, D, tAfter, verify.File.PageCount, 0, out _)!;
        Dictionary<long, int?> sizes = [];
        for (readCur.MoveFirst(); !readCur.Eof; readCur.MoveNext())
        {
            long id = readCur.Read(tAfter.Columns[0]).Value.Integer;
            NativeValue v = readCur.Read(tAfter.Columns[1]).Value;
            Assert.NotEqual(NativeKind.Failed, v.Kind);
            sizes[id] = v.Kind == NativeKind.Null ? null : v.Text!.Length * 2;
        }
        Assert.Equal(2, sizes.Count);
        Assert.Equal(100, sizes[1]);
        Assert.Null(sizes[2]);
    }

    /// <summary>
    /// Named refusals for every remaining unrecorded (Open) long-value case across the R-FMT-2 LV notes after
    /// r-q22-lv-open (r-q22-specific records — UPDATE freeing a chain, MEMO WITH COMPRESSION, and two LV free-map
    /// pages with room — are pinned in <see cref="Long_values_r_q22_open_cases_refuse_by_name"/>):
    /// (1) 0-byte Memo and 0-byte OLE,
    /// (2) single LVAL row leaving 179..263 free bytes on an LVAL page (while &lt;= 178 leaves and &gt;= 264 stays),
    /// (3) multi-page LV free-map walk when the first page has no room,
    /// (4) deleting/replacing a non-top single LVAL row,
    /// (5) emptying an LVAL page by removing its sole single row,
    /// (6) removing a single LVAL row from a page not in the LV free map,
    /// (7) replacing a stored long value when the rewritten data row overflows its data page,
    /// (8) writing or removing (including inline) a long value in an MDB file,
    /// (9) freeing a shared/multi-slot chain page,
    /// (10) two &lt;= 64-byte long-value columns together pushing a row over 4,060 bytes (spill order Open).
    /// </summary>
    [Fact]
    public void Long_value_writes_refuse_every_unrecorded_case_by_name()
    {
        static UpdateOutcome TryInsertLv(Session s, int id, bool memo, int byteLength)
        {
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cursor, s.Workspace);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(t.Columns[0], I4(id)).Succeeded);
            NativeVariant val = MakeLvVariant(memo, byteLength, out nint owned);
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                return frame.Update();
            }
            finally
            {
                if (owned != 0)
                {
                    Bstrs.Free(owned);
                }
            }
        }

        static UpdateOutcome TryDeleteById(Session s, int id)
        {
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cursor, s.Workspace);
            for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(t.Columns[0]).Value.Integer != id; cursor.MoveNext())
            {
            }
            Assert.False(cursor.Eof);
            return frame.Delete();
        }

        static UpdateOutcome TryUpdateById(Session s, int id, bool memo, int newBytes)
        {
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var ctx = new TableEditContext(semantics, new StoredUniqueProbe(s.Cache, D, t, s.File.PageCount), 7177, false, new TableFootprints(s.Cache, D, t, s.File.PageCount));
            using TableCursor cursor = TableCursor.Open(s.Cache, D, t, s.File.PageCount, 0, out _)!;
            using var frame = new EditFrame(ctx, cursor, s.Workspace);
            for (cursor.MoveFirst(); !cursor.Eof && cursor.Read(t.Columns[0]).Value.Integer != id; cursor.MoveNext())
            {
            }
            Assert.False(cursor.Eof);
            Assert.True(frame.Edit().Succeeded);
            nint owned = 0;
            NativeVariant val = newBytes == 0 ? new NativeVariant { Vt = (ushort)VarType.Null } : MakeLvVariant(memo, newBytes, out owned);
            try
            {
                Assert.True(frame.Assign(t.Columns[1], val).Succeeded);
                return frame.Update();
            }
            finally
            {
                if (owned != 0)
                {
                    Bstrs.Free(owned);
                }
            }
        }

        // 1. 0-byte Memo is recorded (evidence/r-q27-autonumber-txn/memo-zerolength-true-fail: 12-byte inline LV header
        // with length 0, flags 0x8000); 0-byte OLE remains unrecorded and refused by name in FieldEncoder and RowWriter.
        using (var s = new Session(SeedGrowthOpen("lv-inline-memo")))
        {
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            var emptyMemo = new NativeValue(NativeKind.Text) { Text = "" };
            Assert.True(FieldEncoder.TryEncodeVariable(emptyMemo, t.Columns[1], D, out byte[]? encodedMemo, out string? encErr), encErr);
            Assert.Equal(LongValuePlan.EncodeInline([]), encodedMemo);
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var writer = new SameWidthWriter(new TableFootprints(s.Cache, D, t, s.File.PageCount));
            CompiledChange compiled = writer.CompileInsert(semantics, [new NativeValue(NativeKind.Long) { Integer = 1 }, emptyMemo], null);
            Assert.NotNull(compiled.Change);
            Assert.Null(compiled.NotWritten);

            UpdateOutcome r = TryInsertLv(s, 1, memo: true, byteLength: 0);
            Assert.True(r.Succeeded, r.ToString());
        }
        using (var s = new Session(SeedGrowthOpen("lv-inline-ole")))
        {
            TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            var emptyOle = new NativeValue(NativeKind.LongBinary) { Bytes = [] };
            Assert.False(FieldEncoder.TryEncodeVariable(emptyOle, t.Columns[1], D, out _, out string? encErr));
            Assert.Contains("0-byte", encErr);
            using var semantics = MutationSemantics.Compile("V", t, null, new NoRules(), D.IndexDescriptor);
            var writer = new SameWidthWriter(new TableFootprints(s.Cache, D, t, s.File.PageCount));
            CompiledChange compiled = writer.CompileInsert(semantics, [new NativeValue(NativeKind.Long) { Integer = 1 }, emptyOle], null);
            Assert.Null(compiled.Change);
            Assert.Contains("0-byte", compiled.NotWritten);
        }

        // 2. LV free-map drop bound (r-q22-lv-open): 178 leaves, 264 stays, 179..263 refuses by name.
        // Start with page 45 holding 1,978 free bytes (after 100B and 2,000B OLE inserts):
        {
            byte[] baseImg = SeedGrowthOpen("lv-fill-2000-1976");
            foreach ((int id, int len) in new[] { (1, 100), (2, 2000) })
            {
                byte[] p1 = baseImg.AsSpan(D.PageSize, D.PageSize).ToArray();
                using var s = new Session(baseImg);
                Assert.True(TryInsertLv(s, id, memo: false, len).Succeeded);
                CloseLvSession(s, p1);
                baseImg = s.File.Snapshot();
            }

            // 1,797B (+2 = 1,799) -> 179 free -> refused.
            using (var s179 = new Session(baseImg.ToArray()))
            {
                UpdateOutcome r = TryInsertLv(s179, 3, memo: false, 1797);
                Assert.False(r.Succeeded);
                Assert.Equal(MutationOpen.Publication, r.Open);
                Assert.Contains("between 179 and 263", r.Detail);
            }

            // 1,713B (+2 = 1,715) -> 263 free -> refused.
            using (var s263 = new Session(baseImg.ToArray()))
            {
                UpdateOutcome r = TryInsertLv(s263, 3, memo: false, 1713);
                Assert.False(r.Succeeded);
                Assert.Equal(MutationOpen.Publication, r.Open);
                Assert.Contains("between 179 and 263", r.Detail);
            }

            // 1,798B (+2 = 1,800) -> 178 free -> succeeds and clears page 45 from LV free map.
            using (var s178 = new Session(baseImg.ToArray()))
            {
                UpdateOutcome ok = TryInsertLv(s178, 3, memo: false, 1798);
                Assert.True(ok.Succeeded, ok.Detail);
                TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s178.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                Assert.Empty(UsageMaps.Pages(s178.Cache, D, t.LongValueMaps.Single().FreeMap, s178.File.PageCount, out _)!);
            }

            // 1,712B (+2 = 1,714) -> 264 free -> succeeds and keeps page 45 in LV free map.
            using (var s264 = new Session(baseImg.ToArray()))
            {
                UpdateOutcome ok = TryInsertLv(s264, 3, memo: false, 1712);
                Assert.True(ok.Succeeded, ok.Detail);
                TableDefinition t = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s264.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
                Assert.Equal([45u], UsageMaps.Pages(s264.Cache, D, t.LongValueMaps.Single().FreeMap, s264.File.PageCount, out _)!);
            }
        }

        // 3. Multi-page LV free-map walk when the first page has no room: after lv-fill-2000-1978's 3 inserts,
        // LV free map holds {45, 47} and page 45 has 1,978 free; inserting 2,000B does not fit on 45 and 47 follows.
        {
            byte[] img = SeedGrowthOpen("lv-fill-2000-1978");
            int[] setup = [100, 2000, 1978];
            for (int i = 0; i < setup.Length; i++)
            {
                byte[] p1 = img.AsSpan(D.PageSize, D.PageSize).ToArray();
                using var s = new Session(img);
                UpdateOutcome ok = TryInsertLv(s, i + 1, memo: false, setup[i]);
                Assert.True(ok.Succeeded, ok.Detail);
                CloseLvSession(s, p1);
                img = s.File.Snapshot();
            }
            using var s4 = new Session(img);
            UpdateOutcome r = TryInsertLv(s4, 4, memo: false, 2000);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("walk order over several LV free-map pages", r.Detail);
        }

        // 4. Deleting/replacing a non-top single LVAL row (ID=2 holds slot 1 on page 45, end 3,996 != 4,096).
        using (var s = new Session(SeedGrowthOpen("lv-op-single-larger")))
        {
            UpdateOutcome r = TryDeleteById(s, 2);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("not the top row", r.Detail);
        }

        // 5. Emptying an LVAL page by removing its sole single row (lv-single-2002 after insert 1).
        {
            byte[] img = SeedGrowthOpen("lv-single-2002");
            using (var s = new Session(img))
            {
                byte[] p1 = img.AsSpan(D.PageSize, D.PageSize).ToArray();
                UpdateOutcome ok = TryInsertLv(s, 1, memo: false, 2002);
                Assert.True(ok.Succeeded, ok.Detail);
                CloseLvSession(s, p1);
                img = s.File.Snapshot();
            }
            using var s2 = new Session(img);
            UpdateOutcome r = TryDeleteById(s2, 1);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("empties LVAL page", r.Detail);
        }

        // 6. Removing a single LVAL row from a page not in the LV free map (after lv-fill-2000-1976 fills page 45 to 0).
        {
            byte[] img = SeedGrowthOpen("lv-fill-2000-1976");
            int[] setup = [100, 2000, 1976];
            for (int i = 0; i < setup.Length; i++)
            {
                byte[] p1 = img.AsSpan(D.PageSize, D.PageSize).ToArray();
                using var s = new Session(img);
                UpdateOutcome ok = TryInsertLv(s, i + 1, memo: false, setup[i]);
                Assert.True(ok.Succeeded, ok.Detail);
                CloseLvSession(s, p1);
                img = s.File.Snapshot();
            }
            using var s4 = new Session(img);
            UpdateOutcome r = TryDeleteById(s4, 1);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("not in the long-value free map", r.Detail);
        }

        // 7. Replacing a stored long value causing the data row to overflow its data page: shrink data page 46's
        // FreeCount to 10 by padding a dummy slot, then replace ID=1 (12-byte single-row ref) with 64-byte inline (76B).
        {
            byte[] img = SeedGrowthOpen("lv-op-single-larger");
            Span<byte> dataPage = img.AsSpan(46 * D.PageSize, D.PageSize);
            int freeNow = (int)D.DataPage.FreeCount.Read(dataPage);
            Assert.True(DataPages.TryAppendRow(dataPage, new byte[freeNow - 12], D, out _, out _));
            using var s = new Session(img);
            UpdateOutcome r = TryUpdateById(s, 1, memo: true, newBytes: 64);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("overflows its data page", r.Detail);
        }

        // 8. MDB file: writing a long value, removing a single-row LV, and removing an inline LV (before early return)
        // all refuse by name.
        {
            byte[] mdbPage0 = ArtifactStore.ForRepository(ReplayFixture.Root()).ReadAll(Jet4MdbFixture).AsSpan(0, D.PageSize).ToArray();
            byte[] img = SeedGrowthOpen("lv-op-single-larger");
            mdbPage0.CopyTo(img, 0);
            using var sWrite = new Session(img);
            UpdateOutcome rWrite = TryInsertLv(sWrite, 4, memo: true, 100);
            Assert.False(rWrite.Succeeded);
            Assert.Equal(MutationOpen.Publication, rWrite.Open);
            Assert.Contains("MDB", rWrite.Detail);

            using var sDel = new Session(img);
            UpdateOutcome rDel = TryDeleteById(sDel, 1);
            Assert.False(rDel.Succeeded);
            Assert.Equal(MutationOpen.Publication, rDel.Open);
            Assert.Contains("MDB", rDel.Detail);

            byte[] inlineImg = SeedGrowthOpen("lv-inline-memo");
            using (var s1 = new Session(inlineImg))
            {
                Assert.True(TryInsertLv(s1, 1, memo: true, 20).Succeeded);
                inlineImg = s1.File.Snapshot();
            }
            mdbPage0.CopyTo(inlineImg, 0);
            using var sMdb = new Session(inlineImg);
            UpdateOutcome rInlineDel = TryDeleteById(sMdb, 1);
            Assert.False(rInlineDel.Succeeded);
            Assert.Equal(MutationOpen.Publication, rInlineDel.Open);
            Assert.Contains("removing a long value in an MDB file", rInlineDel.Detail);
        }

        // 9. Shared / multi-slot chain page: append a second dummy slot to chain page 48 of lv-op-delete-row-chain.
        {
            byte[] img = SeedGrowthOpen("lv-op-delete-row-chain");
            Span<byte> chainPage48 = img.AsSpan(48 * D.PageSize, D.PageSize);
            Assert.True(DataPages.TryAppendRow(chainPage48, [1, 2, 3, 4], D, out _, out _, lval: true));
            using var s = new Session(img);
            UpdateOutcome r = TryDeleteById(s, 3);
            Assert.False(r.Succeeded);
            Assert.Equal(MutationOpen.Publication, r.Open);
            Assert.Contains("multi-slot chain page", r.Detail);
        }

        // 10. Two <= 64-byte long-value columns together pushing a row over 4,060 bytes (qualified.md:598 Open: spill order).
        using (var s = new Session(SeedGrowthOpen("lv-inline-tight-3960", "r-q22-lv-open")))
        {
            TableDefinition baseT = TableDefDecoder.Decode(TableDefSegmentView.Open(42, p => s.Cache.Pin(p).Pin is { } pin ? Session.Copy(pin) : null, D, 8, out _)!, 42, D, out _)!;
            ColumnDescriptor m1 = baseT.Columns.Single(c => c.Name == "M");
            ColumnDescriptor m2 = m1 with
            {
                Name = "M2",
                PhysicalNumber = baseT.MaxColumns,
                VariableIndex = baseT.VariableColumns,
                ColumnId = baseT.MaxColumns,
                Ordinal = baseT.Columns.Count,
            };
            TableDefinition twoMemoTight = baseT with
            {
                Columns = [.. baseT.Columns, m2],
                MaxColumns = baseT.MaxColumns + 1,
                ColumnCount = baseT.ColumnCount + 1,
                VariableColumns = baseT.VariableColumns + 1,
            };
            using var sem = MutationSemantics.Compile("R", twoMemoTight, null, new NoRules(), D.IndexDescriptor);
            var writer = new SameWidthWriter(new TableFootprints(s.Cache, D, twoMemoTight, s.File.PageCount));
            List<NativeValue> vals = [new NativeValue(NativeKind.Long) { Integer = 1 }];
            for (int k = 1; k <= 7; k++)
            {
                vals.Add(new NativeValue(NativeKind.Text) { Text = new string('€', 255) });
            }
            vals.Add(new NativeValue(NativeKind.Text) { Text = new string('€', 195) });
            vals.Add(new NativeValue(NativeKind.Text) { Text = new string('€', 30) });
            vals.Add(new NativeValue(NativeKind.Text) { Text = new string('€', 30) });
            CompiledChange res = writer.CompileInsert(sem, vals, null);
            Assert.Null(res.Change);
            Assert.Contains("spill order", res.NotWritten);
        }
    }

}
