using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Storage;
using Xunit.Abstractions;

namespace CalcDb.Isam.Tests;

/// <summary>
/// W-04/weight-tables-per-sort-order against native ACE files (R-SORT-3 SORT-CHECK; T-IDX-04/06; II-16 §16: decode the
/// existing entries before claiming a match). Every entry of every physical index with a Text or Memo column in the
/// retained fixtures is decoded from its page, and its user key is compared with the key the codec builds from the
/// row's stored values under the column's own sort descriptor. Other orders are counted as refused, never compared.
/// </summary>
public sealed class NativeTextKeyTests(ITestOutputHelper output)
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private static string Root()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    /// <summary>One physical index's comparison: every mismatch's stored values, and a byte detail for the first few.</summary>
    private sealed record IndexTally(string Fixture, string Table, string Index, string Orders, int Entries, int Matched, List<(string[] Values, string? Detail)> Mismatches, bool Refused, string? Failure);

    private sealed record Opened(PageCache Cache, ulong PageCount, CatalogSnapshot Catalog);

    private static Opened? Open(byte[] file)
    {
        if (file.Length < D.PageSize || file.Length % D.PageSize != 0 || ProfileSelector.Select(file.AsSpan(0, D.PageSize), out _) != ProfileRefusal.None)
        {
            return null;
        }
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot? catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _);
        return catalog is null ? null : new Opened(cache, image.PageCount, catalog);
    }

    private static TableDefinition? Definition(PageCache cache, uint page) =>
        TableDefSegmentView.Open(page, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 64, out _) is { } view
            ? TableDefDecoder.Decode(view, page, D, out _)
            : null;

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    /// <summary>The key value of a stored field, as the codec takes it.</summary>
    private static KeyValue ValueOf(NativeValue v, byte type) => v.Kind switch
    {
        NativeKind.Null => KeyValue.Null,
        NativeKind.Text => KeyValue.OfText(v.Text.AsMemory()),
        NativeKind.Boolean => new KeyValue(KeyValueKind.Boolean, Boolean: v.Boolean),
        NativeKind.Currency => new KeyValue(KeyValueKind.ScaledCurrency, Integer: v.Integer),
        NativeKind.Single or NativeKind.Double or NativeKind.DateTime => new KeyValue(KeyValueKind.FloatBits, Bits: v.Bits),
        _ when type is NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long => new KeyValue(KeyValueKind.Integer, Integer: v.Integer),
        _ => throw new InvalidOperationException($"no key value for {v.Kind}"),
    };

    /// <summary>Compare every Text-bearing physical index of every local table (system tables included) of one file.</summary>
    private static List<IndexTally> CompareFile(string fixture, byte[] file, bool includeSystem)
    {
        var tallies = new List<IndexTally>();
        if (Open(file) is not { } db)
        {
            return tallies;
        }
        IEnumerable<CatalogEntry> tables = includeSystem
            ? db.Catalog.Entries.Where(e => e.Type == D.Catalog.LocalTableType)
            : db.Catalog.LocalTables(D);
        foreach (CatalogEntry entry in tables)
        {
            if (Definition(db.Cache, (uint)entry.Id) is not { } table)
            {
                continue;
            }
            foreach (LogicalIndex logical in table.LogicalIndexes.DistinctBy(l => l.PhysicalNumber))
            {
                PhysicalIndex physical = table.PhysicalOf(logical)!;
                ColumnDescriptor?[] columns = [.. physical.Columns.Select(ic => table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber))];
                if (!columns.Any(c => c is not null && IndexKeys.IsText(c.TypeCode)))
                {
                    continue;
                }
                string orders = string.Join(",", columns.Where(c => c?.TextSort is not null).Select(c => c!.TextSort!.Value.ToString()));
                FieldKeyCodec?[] codecs = [.. columns.Select(c => c is null ? null : IndexKeys.CodecFor(c, out _))];
                if (codecs.Any(c => c is null))
                {
                    tallies.Add(new IndexTally(fixture, entry.Name, logical.Name, orders, 0, 0, [], true, null));
                    continue;
                }
                tallies.Add(CompareIndex(fixture, db, table, entry.Name, logical, physical, columns!, [.. codecs.Select(c => c!.Value)], orders));
            }
        }
        return tallies;
    }

    private static IndexTally CompareIndex(string fixture, Opened db, TableDefinition table, string tableName, LogicalIndex logical, PhysicalIndex physical, ColumnDescriptor[] columns, FieldKeyCodec[] codecs, string orders)
    {
        TableCursor cursor = TableCursor.Open(db.Cache, D, table, db.PageCount, 1, out _)!;
        var navigator = new IndexNavigator(db.Cache, D, physical, table.FirstPage);
        if (cursor.SetIndex(logical.Name) != IndexRefusal.None)
        {
            return new IndexTally(fixture, tableName, logical.Name, orders, 0, 0, [], false, "SetIndex failed");
        }
        int entries = 0, matched = 0;
        var mismatches = new List<(string[] Values, string? Detail)>();
        byte[] key = new byte[codecs.Sum(c => c.MaxFieldBytes)];
        for (IndexPosition? at = navigator.First(); at is { } p; at = navigator.Next(p), cursor.MoveNext())
        {
            entries++;
            if (cursor.Current?.Root != p.Row)
            {
                return new IndexTally(fixture, tableName, logical.Name, orders, entries, matched, mismatches, false, $"cursor and navigator diverge at entry {entries}");
            }
            int length = 0;
            var shown = new List<NativeValue>();
            for (int i = 0; i < columns.Length; i++)
            {
                NativeValue v = cursor.Read(columns[i]).Value;
                shown.Add(v);
                Assert.True(IndexKeys.TryWriteField(key.AsSpan(length), codecs[i], physical.Columns[i].Ascending, ValueOf(v, columns[i].TypeCode), out int written));
                length += written;
            }
            length = IndexKeys.FinishComposite(key, length);
            byte[] native = navigator.KeyBytes(p)!;
            if (native.AsSpan().SequenceEqual(key.AsSpan(0, length)))
            {
                matched++;
            }
            else
            {
                string? detail = null;
                if (mismatches.Count < 6)
                {
                    int diff = native.AsSpan().CommonPrefixLength(key.AsSpan(0, length));
                    int from = Math.Max(0, diff - 4);
                    detail = $"differ at {diff}; native {native.Length} B …{Convert.ToHexString(native.AsSpan(from))} encoder {length} B …{Convert.ToHexString(key.AsSpan(from, length - from))}";
                }
                mismatches.Add(([.. shown.Select(v => v.Kind == NativeKind.Text ? v.Text! : v.ToString())], detail));
            }
        }
        return new IndexTally(fixture, tableName, logical.Name, orders, entries, matched, mismatches, false, navigator.LastFailure?.Predicate);
    }

    // Retained fixtures with Text indexes, by the record that produced them (R-SORT-3, R-CURSOR-1). All of the General
    // family are 1033/0 (General - Legacy, also in dbVersion 128 ACCDB files) except create-version-256 (1033/1, General).
    private static readonly (string Record, string Sha)[] GeneralFamily =
    [
        ("r-sort-3/create-keys-accdb", "1969595a7bf160ed36ce7ce610a78a5ae4385f66a945e1fb57cbd0346eeab7e1"),
        ("r-sort-3/create-corpus-accdb", "433b610ae464f91cac0b1c0fe1b261b3db57b1e01e0177cd2045591ead422f3a"),
        ("r-sort-3/create-derive-accdb", "637d8d1a3aa75521438f3079d75d1bde7376e6b89f3fe13e34d1bcdc254ab4e0"),
        ("r-sort-3/create-version-256", "f1d9fdb0baebb4a30cf4ac4f8d7d435cf383c533819e434a4efd9eeece25884f"),
        ("r-sort-3/compact-accdb-to-accdb-general", "b91e314fb2039716af964ee74b760f181ab9bf9628a82fc676fcd688efbda84f"),
        ("r-sort-3/compact-accdb-to-mdb-general", "27cbf6fe272d4dc5e44c2645edefec2b66207e6e9d6f3a77ec6402561e378e66"),
        ("r-sort-3/compact-mdb-to-accdb-general", "1b2b8adc2592944b416ae5246cd706952778118b5d8a8fcb9696da740484a29a"),
        ("r-sort-3b/create-lg", "a9f42cfc94a8907aaa00c139401d8f1a6b5e0619e284de100e3dbbfd9610e629"),
        ("r-sort-3b/create-memo", "75c5396af6bc118572f97337ae8f3033df537b73c2da936c866ecb52164afd6c"),
        ("r-sort-3b/create-mix", "b2ce06328efc99cd7f9d422ec16f173656f75b0a23dce96912884ede2cd8fe51"),
        ("r-sort-3b/create-nulls", "4088866805192c917d92514fd2e19df8721765aa744b43012ef7b5615eba69db"),
        ("r-sort-3b/image-build-order-a", "a59a318ce1aa766e1d3ad4f507f53aaca3030b1aaa1bf5e890daddc0a0eba4ee"),
        ("r-sort-3b/image-drop-rebuild", "1c027b7d31650d4c73b2a4515b6ad8f4e314f1d376c78e74485a94d702a39819"),
        ("r-fix-fmt-sort/sort3-fr-accdb", "a0b127e6b0b5b0ef62fbcf796ac075407b4ed28029294b10de50c0c7412210d2"),
        ("r-fix-fmt-sort/sort3-np-accdb", "15f8763ea016d6299eb5b7fa63bd502238ca2403bf16a1ac73c741ea359c0d95"),
        ("r-cursor-1b/seek-text-index input", TextIndexFixture),
        ("r-cursor-1/seek-duplicates-multileaf input", PagesFixture),
    ];

    // Other orders: 1029 Czech, 1053 Swedish, 1041 Japanese, 1031 German (compaction), 2057 and 1049 (creation).
    private static readonly (string Record, string Sha)[] OtherOrders =
    [
        ("r-sort-3/compact-accdb-to-accdb-czech", "12e34cd24301812f8078ad3e9cc8383d8cfd604ddbe7f204d292d09d1134a214"),
        ("r-sort-3/compact-accdb-to-accdb-swedish", "27de8234e14d56eef2259febed0715b01fa647f36e9e25f0bd20471f55dde1f4"),
        ("r-sort-3/compact-accdb-to-accdb-japanese", "dabd64cc71c83c015fd66e9fb7085a8bb3115ae25e300837850a74791523543b"),
        ("r-sort-3/compact-accdb-to-accdb-german", "067c40ba1c54687d42d202f5b3da231cd8d704f8878d79acee92e58b0e47766d"),
        ("r-sort-3b/create-lu", "d4311d5e0d1e361133a616d1564c4a2f734bee1ecfd0da2d5b7af0344b6640ac"),
        ("r-sort-3b/locale-russian", "853de10412aac52b3011b66c356f0335adec3d726d191d462e9a3a131d0965f0"),
    ];

    private const string TextIndexFixture = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string PagesFixture = "640b1893caaa6a8450731932e84ffa0e2a8be91a00a0be188bae767d7a31db34";

    /// <summary>
    /// The named table residual (SORT-DERIVE, open): ACE keys U+30FC KATAKANA-HIRAGANA PROLONGED SOUND MARK and U+FF70
    /// HALFWIDTH KATAKANA-HIRAGANA PROLONGED SOUND MARK, alone, with primary FF FF under General - Legacy (1033/0), where
    /// the pinned resource gives S4A (the weight of A). r-sort-3/create-corpus-accdb and its compactions. They stay
    /// mismatches: the table is not patched until SORT-DERIVE has their contexts.
    /// </summary>
    private static readonly string[] KanaProlongedMarks = ["\u30FC", "\uFF70"];

    private static byte[] Fixture(string sha) => CalcDb.Evidence.ArtifactStore.ForRepository(Root()).ReadAll(sha);

    [Fact]
    public void Native_General_family_entries_equal_the_encoder_except_the_named_kana_residual()
    {
        int entries = 0, matched = 0, longKeys = 0, general = 0;
        var residual = new List<string>();
        foreach ((string record, string sha) in GeneralFamily)
        {
            foreach (IndexTally t in CompareFile(record, Fixture(sha), includeSystem: true))
            {
                Assert.Null(t.Failure);
                if (t.Refused)
                {
                    // Only r-sort-3/create-keys-accdb's IX_KG: (Text, GUID), and GUID keys are W-04/private-codecs.
                    Assert.Equal(("r-sort-3/create-keys-accdb", "IX_KG"), (t.Fixture, t.Index));
                    continue;
                }
                entries += t.Entries;
                matched += t.Matched;
                general += t.Orders.Contains("1033/1", StringComparison.Ordinal) ? t.Entries : 0;
                longKeys += t.Fixture == "r-sort-3/create-keys-accdb" && t.Table.StartsWith('L') ? t.Matched : 0;
                foreach ((string[] values, string? detail) in t.Mismatches)
                {
                    output.WriteLine($"{t.Fixture} {t.Table}.{t.Index} [{t.Orders}] ({string.Join(", ", values)}) {detail}");
                    Assert.Single(values);
                    Assert.Contains(values[0], KanaProlongedMarks);
                    residual.Add(values[0]);
                }
            }
        }
        output.WriteLine($"{matched} of {entries} native Text-bearing entries equal the encoder ({general} under General 1033/1); {residual.Count} are the kana residual");
        // Every mismatch is the residual, counted: pass means matched the native bytes, and these do not.
        Assert.Equal(entries - matched, residual.Count);
        Assert.True(general > 0 && longKeys > 0);
    }

    /// <summary>
    /// R-SORT-3 SORT-CHECK, the designed values of task b999 (r-sort-3c: kana prolonged sound mark in context, leading
    /// modifiers, expansions before an accent, case flags against positions, secondary sums), ACCDB and MDB under General.
    /// </summary>
    [Fact]
    public void Designed_b999_entries_equal_the_encoder()
    {
        (string Record, string Sha)[] designed =
        [
            ("r-sort-3c/create-accdb", "3cffa138123fa7b1623eab0a240fa3dbc11542f4ef633f17a5406db4f00509cc"),
            ("r-sort-3c/create-mdb", "cf56c817542e7be094d055719aeff989c02afc24d9ff2709d70693f6f360b962"),
        ];
        int entries = 0, matched = 0, residual = 0;
        foreach ((string record, string sha) in designed)
        {
            foreach (IndexTally t in CompareFile(record, Fixture(sha), includeSystem: false))
            {
                Assert.Null(t.Failure);
                Assert.False(t.Refused);
                entries += t.Entries;
                matched += t.Matched;
                foreach ((string[] values, string? detail) in t.Mismatches)
                {
                    output.WriteLine($"{t.Fixture} {t.Table}.{t.Index} [{t.Orders}] ({string.Join(", ", values.Select(v => string.Join(" ", v.Select(c => ((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)))))}) {detail}");
                    // The named residual: the prolonged sound mark, alone or in context (SORT-DERIVE open; these entries are its data).
                    Assert.Single(values);
                    Assert.True(values[0].AsSpan().IndexOfAny(KanaProlongedMarks[0][0], KanaProlongedMarks[1][0]) >= 0, $"not the kana residual: {values[0]}");
                    residual++;
                }
            }
        }
        output.WriteLine($"{matched} of {entries} designed entries equal the encoder; {residual} are the kana prolonged-mark residual");
        // Pass means matched the native bytes: the residual is counted apart, never as matched.
        Assert.Equal(entries - matched, residual);
        Assert.Equal(146, matched);
    }

    [Fact]
    public void Shortened_long_keys_include_their_final_byte_in_the_digest()
    {
        // SORT-CHECK Length510 / Shortened-key collisions: (é×257, Long 1) and (é×257, Long 2) in IX_KN are different
        // stored keys in ACE; the pinned rule would give them one.
        IndexTally kn = CompareFile("r-sort-3/create-keys-accdb", Fixture(GeneralFamily[0].Sha), includeSystem: false).Single(t => t.Table == "LK" && t.Index == "IX_KN");
        Assert.Equal((39, 39), (kn.Entries, kn.Matched));
    }

    [Fact]
    public void Other_orders_refuse_indexed_Text_seek_with_a_count_and_still_read_by_scan()
    {
        // T-IDX-06, T-CAT-05: NotModelled.SortOrder for every Text index of these files, the catalog's included; the
        // position is unchanged; a full scan reads every row.
        foreach ((string record, string sha) in OtherOrders)
        {
            Opened db = Open(Fixture(sha))!;
            int refusedIndexes = 0;
            foreach (CatalogEntry entry in db.Catalog.Entries.Where(e => e.Type == D.Catalog.LocalTableType))
            {
                TableDefinition table = Definition(db.Cache, (uint)entry.Id)!;
                foreach (LogicalIndex logical in table.LogicalIndexes)
                {
                    PhysicalIndex physical = table.PhysicalOf(logical)!;
                    ColumnDescriptor? first = table.Columns.FirstOrDefault(c => c.PhysicalNumber == physical.Columns[0].PhysicalNumber);
                    if (first is null || !IndexKeys.IsText(first.TypeCode))
                    {
                        continue;
                    }
                    Assert.Null(SortDescriptors.Family(first.TextSort!.Value, out _));
                    TableCursor cursor = TableCursor.Open(db.Cache, D, table, db.PageCount, 1, out _)!;
                    Assert.Equal(IndexRefusal.None, cursor.SetIndex(logical.Name));
                    RowToken? before = cursor.Current;
                    long counted = CalcDb.Foundation.Diagnostics.CostCounters.Current.SortOrderTextSeekRefusals;
                    Assert.Equal(IndexRefusal.SortOrderNotModelled, cursor.Seek(SeekOperator.Equal, [new SeekKey(KeyValue.OfText("a".AsMemory()))], out bool found));
                    Assert.False(found);
                    Assert.Equal(counted + 1, CalcDb.Foundation.Diagnostics.CostCounters.Current.SortOrderTextSeekRefusals);
                    Assert.Equal(before, cursor.Current);
                    refusedIndexes++;
                    Assert.Equal(IndexRefusal.None, cursor.SetIndex(null));
                    long rows = 0;
                    for (cursor.MoveFirst(); !cursor.Eof; cursor.MoveNext())
                    {
                        Assert.NotEqual(NativeKind.Failed, cursor.Read(first).Value.Kind);
                        rows++;
                    }
                    Assert.Equal(table.RowCount, (uint)rows);
                }
            }
            Assert.True(refusedIndexes > 0, record);
        }
    }

    private static (TableCursor Cursor, TableDefinition Table) OpenTable(string sha, string name)
    {
        Opened db = Open(Fixture(sha))!;
        CatalogEntry entry = db.Catalog.LocalTables(D).Single(e => e.Name == name);
        TableDefinition table = Definition(db.Cache, (uint)entry.Id)!;
        return (TableCursor.Open(db.Cache, D, table, db.PageCount, 1, out _)!, table);
    }

    private static SeekKey T(string? text) => new(text is null ? KeyValue.Null : KeyValue.OfText(text.AsMemory()));

    private static long? Id(TableCursor c) => c.Current is null ? null : c.Read("ID").Value.Integer;

    [Theory]
    // r-cursor-1b/seek-text-index on F.IX_S: the landing, then MoveNext, then MovePrevious twice (null: no current record).
    [InlineData("=", "abc", 5L, 6L, 4L)]
    [InlineData("=", "ABC", 5L, 6L, 4L)]
    [InlineData("=", "alpha", 1L, 2L, 7L)]
    [InlineData("=", "O'Brien", 3L, null, 2L)]
    [InlineData("=", "abc ", 5L, 6L, 4L)]
    [InlineData("=", "a\"b", 4L, 5L, 8L)]
    [InlineData("=", null, 8L, 4L, null)]
    [InlineData(">=", "ab", 5L, 6L, 4L)]
    [InlineData(">", "abc", 7L, 1L, 6L)]
    [InlineData("<", "abc", 4L, 5L, 8L)]
    [InlineData("<=", "abc", 5L, 6L, 4L)]
    public void Text_Seek_lands_where_ACE_lands(string op, string? key, long landing, long? next, long? backTwice)
    {
        (TableCursor rs, _) = OpenTable(TextIndexFixture, "F");
        Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_S"));
        Assert.Equal(IndexRefusal.None, rs.Seek(Op(op), [T(key)], out bool found));
        Assert.True(found);
        Assert.False(rs.NoMatch);
        Assert.Equal(landing, Id(rs));
        rs.MoveNext();
        Assert.Equal(next, Id(rs));
        rs.MovePrevious();
        rs.MovePrevious();
        Assert.Equal(backTwice, Id(rs));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("")]
    [InlineData("Zed")]
    public void A_missed_Text_Seek_is_NoMatch_as_in_ACE(string key)
    {
        // r-cursor-1b/seek-text-index: NoMatch, then MoveNext has no current record and two MovePrevious reach "beta".
        (TableCursor rs, _) = OpenTable(TextIndexFixture, "F");
        rs.SetIndex("IX_S");
        Assert.Equal(IndexRefusal.None, rs.Seek(SeekOperator.Equal, [T(key)], out bool found));
        Assert.False(found);
        Assert.True(rs.NoMatch);
        rs.MoveNext();
        Assert.Null(Id(rs));
        rs.MovePrevious();
        rs.MovePrevious();
        Assert.Equal(2, Id(rs));
    }

    [Theory]
    // r-cursor-1/seek-duplicates-multileaf on DL.IX_K: a 200-unit key whose duplicates cross leaves.
    [InlineData("=", 1L, 2L, 1001L)]
    [InlineData(">", 1002L, null, 256L)]
    [InlineData("<=", 1L, 2L, 1001L)]
    [InlineData("<", 1001L, 1L, null)]
    public void Text_Seek_over_duplicates_across_leaves_lands_where_ACE_lands(string op, long landing, long? next, long? backTwice)
    {
        (TableCursor rs, _) = OpenTable(PagesFixture, "DL");
        Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_K"));
        Assert.Equal(IndexRefusal.None, rs.Seek(Op(op), [T(new string('k', 200))], out bool found));
        Assert.True(found);
        Assert.Equal(landing, Id(rs));
        rs.MoveNext();
        Assert.Equal(next, Id(rs));
        rs.MovePrevious();
        rs.MovePrevious();
        Assert.Equal(backTwice, Id(rs));
    }

    private const string KeysFixture = "1969595a7bf160ed36ce7ce610a78a5ae4385f66a945e1fb57cbd0346eeab7e1";
    private const string MemoFixture = "75c5396af6bc118572f97337ae8f3033df537b73c2da936c866ecb52164afd6c";

    private static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    [Theory]
    // r-cursor-1-seek-keys/long-partial-keys-accdb (CUR-SEEK): a partial key past 510 bytes takes the stored long-key
    // finish, so `=` is NoMatch and the others land at its digest's place (both é×255 rows on IX_KN come before it).
    [InlineData("IX_KN", "é", 255, "=", null)]
    [InlineData("IX_KN", "é", 255, ">=", 29L)]
    [InlineData("IX_KN", "é", 255, ">", 29L)]
    [InlineData("IX_KN", "é", 255, "<=", 19L)]
    [InlineData("IX_KN", "é", 255, "<", 19L)]
    [InlineData("IX_KN", "ß", 255, "=", null)]
    [InlineData("IX_KN", "ß", 255, ">=", 26L)]
    [InlineData("IX_KN", "ß", 255, "<=", 24L)]
    [InlineData("IX_KN", "一", 255, ">=", 34L)]
    [InlineData("IX_KN", "一", 255, "<", 25L)]
    [InlineData("IX_KND", "é", 255, ">=", 30L)]
    [InlineData("IX_KND", "é", 255, "<=", 20L)]
    [InlineData("IX_KND", "ß", 255, ">", 33L)]
    [InlineData("IX_KND", "ß", 255, "<", 26L)]
    // Controls at and below the 508-byte cut: ordinary prefix bounds.
    [InlineData("IX_KN", "é", 128, "=", 17L)]
    [InlineData("IX_KN", "é", 128, "<", 28L)]
    [InlineData("IX_KN", "A", 255, "=", 5L)]
    [InlineData("IX_KN", "A", 255, "<", 4L)]
    public void A_long_partial_Text_key_lands_where_ACE_lands(string index, string unit, int count, string op, long? landing)
    {
        (TableCursor rs, _) = OpenTable(KeysFixture, "LK");
        Assert.Equal(IndexRefusal.None, rs.SetIndex(index));
        Assert.Equal(IndexRefusal.None, rs.Seek(Op(op), [T(Rep(unit, count))], out bool found));
        Assert.Equal(landing is not null, found);
        Assert.Equal(landing, Id(rs));
    }

    [Theory]
    // r-cursor-1-seek-keys/long-partial-keys-accdb: full (K, N) keys past 510 bytes, ordered by their digests (18, 20, 19, 29).
    [InlineData(1, "=", 19L)]
    [InlineData(1, ">", 29L)]
    [InlineData(1, "<", 20L)]
    [InlineData(2, "=", 20L)]
    [InlineData(2, ">", 19L)]
    [InlineData(2, "<", 18L)]
    public void A_long_full_key_lands_where_ACE_lands(long n, string op, long landing)
    {
        (TableCursor rs, _) = OpenTable(KeysFixture, "LK");
        rs.SetIndex("IX_KN");
        Assert.Equal(IndexRefusal.None, rs.Seek(Op(op), [T(Rep("é", 255)), new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: n))], out _));
        Assert.Equal(landing, Id(rs));
    }

    [Theory]
    // r-cursor-1-seek-keys/over-length-text-keys and r-sort-3b/memo-text255-key on MM.IX_TK (Text(255)): a Seek key is
    // the whole argument less trailing blanks (TextKeyExtent.WholeSeekArgument), never cut to 255 units. The A×255 group
    // is 6..15, then 10, 11 (A×254 B), 12 (A×254 C).
    [InlineData("A", 256, "", "=", null)]
    [InlineData("A", 256, "", ">=", 10L)]
    [InlineData("A", 256, "", "<=", 15L)]
    [InlineData("A", 1000, "", "=", null)]
    [InlineData("A", 5000, "", ">", 10L)]
    [InlineData("a", 256, "", "<", 15L)]
    [InlineData("A", 255, " ", "=", 6L)]
    [InlineData("A", 255, "   ", ">", 10L)]
    [InlineData("A", 255, " ", "<", 4L)]
    [InlineData("A", 254, "Bx", "=", null)]
    [InlineData("A", 254, "Bx", ">=", 12L)]
    [InlineData("A", 254, "Bx", "<=", 11L)]
    [InlineData(" ", 300, "", "=", 2L)]
    [InlineData(" ", 300, "", "<", 1L)]
    public void An_over_length_Text_key_is_compared_whole_as_in_ACE(string unit, int count, string tail, string op, long? landing)
    {
        (TableCursor rs, _) = OpenTable(MemoFixture, "MM");
        Assert.Equal(IndexRefusal.None, rs.SetIndex("IX_TK"));
        Assert.Equal(IndexRefusal.None, rs.Seek(Op(op), [T(Rep(unit, count) + tail)], out bool found));
        Assert.Equal(landing is not null, found);
        Assert.Equal(landing, Id(rs));
    }

    private static SeekOperator Op(string op) => op switch
    {
        "=" => SeekOperator.Equal,
        ">=" => SeekOperator.GreaterOrEqual,
        ">" => SeekOperator.Greater,
        "<=" => SeekOperator.LessOrEqual,
        _ => SeekOperator.Less,
    };
}
