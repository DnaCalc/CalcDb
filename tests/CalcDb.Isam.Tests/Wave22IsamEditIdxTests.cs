using System.Runtime.InteropServices;
using System.Security.Cryptography;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Tests.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Isam.Tests;

/// <summary>
/// Wave 22 ISAM / Format parity tests (W-05, R-FMT-2, R-CURSOR-1, R-CAT-1):
/// 1) Deleted-row detection (<see cref="CursorFailure.DeletedRecord"/> / 3167) after page-retiring <c>DeleteEveryRow</c>
///    (evidence/r-cursor-1b/requery-empty-to-populated, evidence/r-q17-growth/retire-delete-au1).
/// 2) <c>UPDATE</c> rewriting rows recorded before <c>ALTER TABLE ADD COLUMN</c>
///    (evidence/r-str-ado-act/str-schema-change call #29).
/// 3) Table-opened dynasets using PK / index order while snapshots and forward-only cursors use storage order
///    (evidence/r-cursor-1b/percent-partial-population #31/#63, evidence/r-q2-cursor-dao/id-bookmark-number-map #58/#61).
/// 4) Clean named refusals preserved for unrecorded chained-LVAL recordset <c>Edit/Update</c> and MDB chained Memo writes
///    (evidence/r-str-ado-act/str-edit-requery #37, evidence/r-q-wire/str-edit-requery-mdb #18).
/// </summary>
public sealed class Wave22IsamEditIdxTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private const string RCursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6"; // r-cursor-1b-main.accdb
    private const string StrAccdb = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7"; // r-str-ado-act-str-accdb.accdb
    private const string StrMdb = "f0f9b91a15eb661cf9093e0e6b4871ac963129355578d1bb9fbb1ea5b98206cb"; // r-str-ado-act-str-mdb.mdb
    private const string WAccdb = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be"; // r-fmt-2b create-w-accdb
    private const string Q2Main = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    private static void WithBstr(string value, Action<NativeVariant> use)
    {
        nint bstr = Marshal.StringToBSTR(value);
        try
        {
            use(new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private static string FindRepoRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        return root;
    }

    private static ReadOnlyMemory<byte> Copy(PagePin pin)
    {
        using (pin)
        {
            return pin.Bytes.ToArray();
        }
    }

    private static long ReadId(IRecordCursor cursor)
    {
        FieldResult res = cursor.Read(cursor.Table.Columns.Single(c => c.Name == "ID"));
        Assert.Null(res.Failure);
        return res.Value.Integer;
    }

    private static (PageCache Cache, ulong Pages, TableDefinition Table) OpenReadOnly(string sha256, string tableName)
    {
        byte[] file = ArtifactStore.ForRepository(FindRepoRoot()).ReadAll(sha256);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).Single(e => e.Name == tableName);
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!,
            (uint)entry.Id,
            D,
            out _)!;
        return (cache, image.PageCount, table);
    }

    private static (MemoryDatabaseFile Image, PageCache Cache, CatalogSnapshot Catalog, DatabaseHeader Header, IPublicationTarget Target)
        CreateWritableHost(byte[] bytes)
    {
        var image = new MemoryDatabaseFile(bytes, D.PageSize);
        var cache = new PageCache(image, image.Incarnation, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _, readProperties: true)!;
        DatabaseHeader header = DatabaseHeader.Decode(bytes.AsSpan(0, D.PageSize), D);
        IPublicationTarget target = new MemoryTarget(image);
        return (image, cache, catalog, header, target);
    }

    private static (TableDefinition Table, TableEditContext Context) CompileTableEdit(
        MemoryDatabaseFile image,
        PageCache cache,
        CatalogSnapshot catalog,
        DatabaseHeader header,
        string tableName)
    {
        CatalogEntry entry = catalog.LocalTables(D).Single(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p => cache.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!,
            (uint)entry.Id,
            D,
            out _)!;
        PropertyBlob? blob = null;
        if (entry.Properties is { Length: > 0 } raw)
        {
            blob = PropertyBlob.Read(raw, out _);
        }
        var binder = new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query));
        MutationSemantics semantics = MutationSemantics.Compile(
            entry.Name,
            table,
            blob,
            binder,
            D.IndexDescriptor,
            []);
        var ctx = new TableEditContext(
            semantics,
            new StoredUniqueProbe(cache, D, table, image.PageCount),
            7177,
            hasRelationships: false,
            new TableFootprints(cache, D, table, image.PageCount));
        return (table, ctx);
    }

    private sealed class MemoryTarget(MemoryDatabaseFile image) : IPublicationTarget
    {
        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
        {
            failure = null;
            return image;
        }
    }

    /// <summary>
    /// evidence/r-cursor-1b/requery-empty-to-populated (calls #50, #58, #109, #117, #168, #176):
    /// <c>DELETE FROM E</c> (<see cref="EditFrame.DeleteEveryRow"/>) releases the data page from <c>OwnedMap</c> while
    /// leaving the data-page slots untouched (r-q17-growth/retire-delete-au1). Pre-positioned live cursors (TableCursor
    /// and Dynaset) must fail reads with <see cref="CursorFailure.DeletedRecord"/> (DAO 3167); a Snapshot still reads
    /// its view (1); and Dynaset/Snapshot <c>Requery</c> lands empty (BOF/EOF/3021).
    /// </summary>
    [Fact]
    public void DeleteEveryRow_invalidates_stale_TableCursor_and_PopulationCursor_with_DeletedRecord_while_Snapshot_keeps_reading()
    {
        using var fixture = new ReplayFixture(RCursor1bMain);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("E")!.Value;
        ColumnDescriptor idCol = table.Columns.Single(c => c.Name == "ID");
        ColumnDescriptor textCol = table.Columns[1];

        var ws = new TransactionCoordinator(new LockerId(1))
        {
            Publisher = fixture,
            Scheduler = new CountingScheduler(),
            Clock = static () => TimeSpan.Zero,
        };

        using (var writerCursor = (TableCursor)fixture.Open(table, 1)!)
        {
            var addFrame = new EditFrame(ctx, writerCursor, ws);
            Assert.True(addFrame.AddNew().Succeeded);
            Assert.True(addFrame.Assign(idCol, I4(1)).Succeeded);
            WithBstr("a", v => Assert.True(addFrame.Assign(textCol, v).Succeeded));
            Assert.True(addFrame.Update().Succeeded);

            Assert.True(addFrame.AddNew().Succeeded);
            Assert.True(addFrame.Assign(idCol, I4(2)).Succeeded);
            WithBstr("b", v => Assert.True(addFrame.Assign(textCol, v).Succeeded));
            Assert.True(addFrame.Update().Succeeded);
        }

        // Verify TableCursor reads row 2 (calls #26-#42), Dynaset reads row 1, Snapshot reads row 1.
        using var rsTable = (TableCursor)fixture.Open(table, 1)!;
        Assert.Equal(MoveOutcome.Moved, rsTable.MoveLast());
        Assert.Equal(2, ReadId(rsTable));

        using var rsDyn = (PopulationCursor)fixture.Open(table, 2)!;
        Assert.Equal(1, ReadId(rsDyn));

        using var rsSnap = (PopulationCursor)fixture.Open(table, 4)!;
        Assert.Equal(1, ReadId(rsSnap));

        // Call #45: DELETE FROM E without WHERE -> CompileRelease removes data page from OwnedMap.
        using (var delCursor = (TableCursor)fixture.Open(table, 1)!)
        {
            var delFrame = new EditFrame(ctx, delCursor, ws);
            using StatementScope statement = ws.BeginStatement();
            UpdateOutcome delOut = delFrame.DeleteEveryRow(out int deleted);
            Assert.True(delOut.Succeeded);
            Assert.Equal(2, deleted);
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        // Calls #50/#58: stale TableCursor reads DeletedRecord (3167) before and after Move/Rebind.
        Assert.Equal(CursorFailure.DeletedRecord, rsTable.Read(idCol).Failure);
        Assert.Equal(CursorFailure.DeletedRecord, rsTable.Read(textCol).Failure);

        // Call #109: stale Dynaset reads DeletedRecord (3167) even though row 1 had already been bound.
        Assert.Equal(CursorFailure.DeletedRecord, rsDyn.Read(idCol).Failure);

        // Call #112/#117: Dynaset Requery lands empty (BOF=True, EOF=True, NoCurrentRecord = 3021).
        Assert.True(rsDyn.Requery(out _));
        Assert.True(rsDyn.Bof);
        Assert.True(rsDyn.Eof);
        Assert.Equal(0, rsDyn.RecordCount);
        Assert.Equal(CursorFailure.NoCurrentRecord, rsDyn.Read(idCol).Failure);

        // Call #168/#176: Snapshot keeps reading its fixed view (1) until Snapshot Requery clears it.
        Assert.Equal(1, ReadId(rsSnap));
        Assert.True(rsSnap.Requery(out _));
        Assert.True(rsSnap.Bof);
        Assert.True(rsSnap.Eof);
        Assert.Equal(0, rsSnap.RecordCount);
        Assert.Equal(CursorFailure.NoCurrentRecord, rsSnap.Read(idCol).Failure);
    }

    /// <summary>
    /// Overlay coverage for <see cref="RowBinding.BoundImagesCurrent"/>: if a pre-bound cursor sits inside an
    /// explicit transaction and <c>DeleteEveryRow</c> clears <c>OwnedMap</c> inside that overlay, subsequent reads
    /// must re-evaluate ownership and return <see cref="CursorFailure.DeletedRecord"/>; rolling back restores visibility.
    /// </summary>
    [Fact]
    public void DeleteEveryRow_inside_BeginTrans_overlay_invalidates_already_bound_cursor_and_Rollback_restores_row()
    {
        using var fixture = new ReplayFixture(RCursor1bMain);
        (TableDefinition table, TableEditContext ctx) = fixture.Table("E")!.Value;
        ColumnDescriptor idCol = table.Columns.Single(c => c.Name == "ID");
        ColumnDescriptor textCol = table.Columns[1];

        var ws = new TransactionCoordinator(new LockerId(1))
        {
            Publisher = fixture,
            Scheduler = new CountingScheduler(),
            Clock = static () => TimeSpan.Zero,
        };

        using (var setupCursor = (TableCursor)fixture.Open(table, 1)!)
        {
            var setupFrame = new EditFrame(ctx, setupCursor, ws);
            Assert.True(setupFrame.AddNew().Succeeded);
            Assert.True(setupFrame.Assign(idCol, I4(1)).Succeeded);
            WithBstr("a", v => Assert.True(setupFrame.Assign(textCol, v).Succeeded));
            Assert.True(setupFrame.Update().Succeeded);
        }

        Assert.Null(ws.BeginTrans());
        var overlaySource = new PageSource(fixture.Cache, ws);
        using var liveInTxn = TableCursor.Open(overlaySource, D, table, fixture.Image.PageCount, 0, out _)!;
        Assert.Equal(1, ReadId(liveInTxn)); // pre-binds _boundOwnedMap against overlay image

        using (var delCursor = TableCursor.Open(overlaySource, D, table, fixture.Image.PageCount, 0, out _)!)
        {
            var delFrame = new EditFrame(ctx, delCursor, ws);
            using StatementScope statement = ws.BeginStatement();
            UpdateOutcome delOut = delFrame.DeleteEveryRow(out int deleted);
            Assert.True(delOut.Succeeded);
            Assert.Equal(1, deleted);
            Assert.Equal(MutationOpen.None, statement.Complete().Open);
        }

        Assert.Equal(CursorFailure.DeletedRecord, liveInTxn.Read(idCol).Failure);
        Assert.True(ws.Rollback().Succeeded);
        Assert.Equal(1, ReadId(liveInTxn));
    }

    /// <summary>
    /// evidence/r-str-ado-act/str-schema-change (call #25 <c>ALTER TABLE S ADD COLUMN X TEXT(10)</c>, call #29
    /// <c>UPDATE S SET X = Left(T, 10) WHERE ID IN (3, 4, 5)</c>, call #31): pre-ADD-COLUMN rows have
    /// <c>envelope.ColumnCount &lt; table.MaxColumns</c> while <c>table.Columns.Count == table.MaxColumns</c>.
    /// Updating the newly added column rewrites those rows to <c>C = MaxColumns</c> and reads back ACE's values;
    /// also verifies untouched fixed columns added alongside stay Null without touching missing fixed slots, while
    /// tables with dropped/altered column gaps (<c>Columns.Count != MaxColumns</c>) remain refused.
    /// </summary>
    [Fact]
    public void Update_pre_add_column_rows_rewrites_envelope_to_MaxColumns_and_keeps_dropped_gap_guard()
    {
        byte[] initialBytes = ArtifactStore.ForRepository(FindRepoRoot()).ReadAll(StrAccdb);
        (MemoryDatabaseFile image, PageCache cache, CatalogSnapshot catalog, DatabaseHeader header, IPublicationTarget target) =
            CreateWritableHost(initialBytes);
        using (image)
        {
            var ws = new TransactionCoordinator(new LockerId(1))
            {
                Publisher = target,
                Scheduler = new CountingScheduler(),
                Clock = static () => TimeSpan.Zero,
            };

            // Call #25: ALTER TABLE S ADD COLUMN X TEXT(10)
            var schemaWriter = new SchemaWriter(cache, D, catalog, image.PageCount, header.SortOrder);
            SchemaOutcome ddl1 = schemaWriter.AlterAddColumn(ws, new AlterAddColumnRequest("S", new NewField("X", NewFieldType.Text, Size: 10), SchemaRoute.Sql), dateUpdateBits: 0);
            Assert.True(ddl1.Succeeded, ddl1.Refused?.ToString());
            ws.EndLazyBurst(cache);
            if (ws.NeedsCloseCommit(cache))
            {
                Assert.Equal(MutationOpen.None, ws.PublishCloseCommit(cache).Open);
            }

            // Re-open after ALTER TABLE ADD COLUMN X TEXT(10) + test second ADD COLUMN Y LONG (untouched added fixed column).
            (MemoryDatabaseFile image2, PageCache cache2, CatalogSnapshot catalog2, DatabaseHeader header2, IPublicationTarget target2) =
                CreateWritableHost(image.Snapshot());
            using (image2)
            {
                var ws2 = new TransactionCoordinator(new LockerId(1))
                {
                    Publisher = target2,
                    Scheduler = new CountingScheduler(),
                    Clock = static () => TimeSpan.Zero,
                };
                var schemaWriter2 = new SchemaWriter(cache2, D, catalog2, image2.PageCount, header2.SortOrder);
                SchemaOutcome ddl2 = schemaWriter2.AlterAddColumn(ws2, new AlterAddColumnRequest("S", new NewField("Y", NewFieldType.Long), SchemaRoute.Sql), dateUpdateBits: 0);
                Assert.True(ddl2.Succeeded, ddl2.Refused?.ToString());
                ws2.EndLazyBurst(cache2);
                if (ws2.NeedsCloseCommit(cache2))
                {
                    Assert.Equal(MutationOpen.None, ws2.PublishCloseCommit(cache2).Open);
                }

                CatalogSnapshot refreshedCatalog = CatalogBootstrap.Open(cache2, D, image2.PageCount, out _, readProperties: true)!;
                (TableDefinition sTable, TableEditContext sCtx) = CompileTableEdit(image2, cache2, refreshedCatalog, header2, "S");
                using (sCtx.Semantics)
                {
                    Assert.Equal(sTable.MaxColumns, sTable.Columns.Count);
                    ColumnDescriptor idCol = sTable.Columns.Single(c => c.Name == "ID");
                    ColumnDescriptor xCol = sTable.Columns.Single(c => c.Name == "X");
                    ColumnDescriptor yCol = sTable.Columns.Single(c => c.Name == "Y");

                    // Pre-ADD-COLUMN rows recorded fewer columns than MaxColumns.
                    var source = new PageSource(cache2);
                    using (var verifyScan = TableCursor.Open(source, D, sTable, image2.PageCount, 0, out _)!)
                    {
                        for (MoveOutcome m = verifyScan.MoveFirst(); m == MoveOutcome.Moved; m = verifyScan.MoveNext())
                        {
                            long id = verifyScan.Read(idCol).Value.Integer;
                            if (id is 3 or 4 or 5)
                            {
                                using RowView? rv = RowResolver.Resolve(source, D, verifyScan.CurrentRow!.Value, out _);
                                Assert.True(RowEnvelope.TryOpen(rv!.Value.Body, D, out RowEnvelope env, out _));
                                Assert.True(env.ColumnCount < sTable.MaxColumns);
                            }
                        }
                    }

                    // Call #29 oracle outputs for rows 3, 4, 5: Left(T, 10) -> "a\0b", "\0", "abc\0".
                    (long Id, string X)[] expected =
                    [
                        (3, "a\0b"),
                        (4, "\0"),
                        (5, "abc\0"),
                    ];
                    using (var editCursor = TableCursor.Open(source, D, sTable, image2.PageCount, 0, out _)!)
                    {
                        var frame = new EditFrame(sCtx, editCursor, ws2);
                        foreach ((long id, string text) in expected)
                        {
                            for (editCursor.MoveFirst(); editCursor.Read(idCol).Value.Integer != id; editCursor.MoveNext())
                            {
                            }
                            Assert.True(frame.Edit().Succeeded);
                            WithBstr(text, v => Assert.True(frame.Assign(xCol, v).Succeeded));
                            if (id == 3)
                            {
                                // Also verify assigning the newly added fixed Long column on row 3 works, while leaving
                                // Y Null on rows 4 and 5 exercises an untouched absent fixed column across RewrittenRow.
                                Assert.True(frame.Assign(yCol, I4(42)).Succeeded);
                            }
                            UpdateOutcome updated = frame.Update();
                            Assert.True(updated.Succeeded, updated.Detail);
                        }
                    }

                    // Re-read published bytes with a fresh PageCache to verify persisted row encodings.
                    (MemoryDatabaseFile image3, PageCache cache3, CatalogSnapshot catalog3, _, _) =
                        CreateWritableHost(image2.Snapshot());
                    using (image3)
                    {
                        CatalogEntry entry = catalog3.LocalTables(D).Single(e => e.Name == "S");
                        TableDefinition readBackTable = TableDefDecoder.Decode(
                            TableDefSegmentView.Open((uint)entry.Id, p => cache3.Pin(p).Pin is { } pin ? Copy(pin) : null, D, 8, out _)!,
                            (uint)entry.Id,
                            D,
                            out _)!;
                        using var verify = TableCursor.Open(new PageSource(cache3), D, readBackTable, image3.PageCount, 0, out _)!;
                        var seen = new Dictionary<long, (string? X, NativeValue Y)>();
                        for (MoveOutcome m = verify.MoveFirst(); m == MoveOutcome.Moved; m = verify.MoveNext())
                        {
                            long id = verify.Read(readBackTable.Columns.Single(c => c.Name == "ID")).Value.Integer;
                            if (id is 3 or 4 or 5)
                            {
                                NativeValue xv = verify.Read(readBackTable.Columns.Single(c => c.Name == "X")).Value;
                                NativeValue yv = verify.Read(readBackTable.Columns.Single(c => c.Name == "Y")).Value;
                                seen[id] = (xv.Text, yv);
                            }
                        }
                        Assert.Equal("a\0b", seen[3].X);
                        Assert.Equal(42, seen[3].Y.Integer);
                        Assert.Equal("\0", seen[4].X);
                        Assert.Equal(NativeKind.Null, seen[4].Y.Kind);
                        Assert.Equal("abc\0", seen[5].X);
                        Assert.Equal(NativeKind.Null, seen[5].Y.Kind);

                        // Dropped-column gap guard (Columns.Count != MaxColumns) still refuses changing an absent column on ID=1.
                        TableDefinition droppedGapTable = readBackTable with
                        {
                            MaxColumns = readBackTable.MaxColumns + 1,
                        };
                        var gapBinder = new ExpressionRuleBinder(new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header2.SortOrder), ExpressionUse.Query));
                        using MutationSemantics gapSemantics = MutationSemantics.Compile("S", droppedGapTable, null, gapBinder, D.IndexDescriptor, []);
                        var gapCtx = new TableEditContext(gapSemantics, new StoredUniqueProbe(cache3, D, droppedGapTable, image3.PageCount), 7177, false, new TableFootprints(cache3, D, droppedGapTable, image3.PageCount));
                        var gapWs = new TransactionCoordinator(new LockerId(2)) { Publisher = new MemoryTarget(image3), Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
                        using var gapCursor = TableCursor.Open(new PageSource(cache3), D, droppedGapTable, image3.PageCount, 0, out _)!;
                        ColumnDescriptor gapId = droppedGapTable.Columns.Single(c => c.Name == "ID");
                        ColumnDescriptor gapY = droppedGapTable.Columns.Single(c => c.Name == "Y");
                        for (gapCursor.MoveFirst(); gapCursor.Read(gapId).Value.Integer != 1; gapCursor.MoveNext())
                        {
                        }
                        var gapFrame = new EditFrame(gapCtx, gapCursor, gapWs);
                        Assert.True(gapFrame.Edit().Succeeded);
                        Assert.True(gapFrame.Assign(gapY, I4(99)).Succeeded);
                        UpdateOutcome gapRefused = gapFrame.Update();
                        Assert.False(gapRefused.Succeeded);
                        Assert.Equal(MutationOpen.Publication, gapRefused.Open);
                        Assert.Contains("absent from row", gapRefused.Detail);
                    }
                }
            }
        }
    }

    /// <summary>
    /// evidence/r-cursor-1b/percent-partial-population (call #31 Dynaset 50% -> ID 1025 vs call #63 Snapshot 50% -> ID 1007)
    /// and evidence/r-q2-cursor-dao/id-bookmark-number-map (call #58/#61 Dynaset Move 600 -> Bookmark f7040000, ID 701):
    /// a table-opened keyset Dynaset selects <c>PK_Big</c> order, whereas a Snapshot uses storage order.
    /// Also verifies Dynaset <c>Requery</c> keeps index order and <c>&gt;= 2</c>-row unrecorded index shapes remain cleanly
    /// refused (<c>NotModelled.DynasetOrder.*</c>) while <c>&lt; 2</c> rows fall back to storage order.
    /// </summary>
    [Fact]
    public void Dynaset_table_cursor_uses_PK_order_matching_percent_and_bookmark_records_while_snapshot_uses_storage_order()
    {
        (PageCache cache, ulong pages, TableDefinition big) = OpenReadOnly(Q2Main, "Big");
        var source = new PageSource(cache);

        using PopulationCursor dyn = PopulationCursor.Open(source, D, big, pages, forwardOnly: false, out TraversalFailure? dynFail, bookmarkBase: KeysetBookmark.FirstBase, snapshot: false)!;
        Assert.Null(dynFail);
        Assert.Equal("00020000", Convert.ToHexStringLower(dyn.GetBookmark(out _)!));
        Assert.Equal(1, ReadId(dyn));

        // r-q2-cursor-dao/id-bookmark-number-map calls #50..#61: 6x Move(100) lands at ordinal 600 (93040000, ID=601);
        // 7th Move(100) lands at keyset ordinal 700 -> bookmark f7040000, ID=701.
        for (int step = 0; step < 6; step++)
        {
            Assert.Equal(MoveOutcome.Moved, dyn.Move(100));
        }
        Assert.Equal(600, dyn.Ordinal);
        Assert.Equal("93040000", Convert.ToHexStringLower(dyn.GetBookmark(out _)!));
        Assert.Equal(601, ReadId(dyn));
        Assert.Equal(MoveOutcome.Moved, dyn.Move(100));
        Assert.Equal(700, dyn.Ordinal);
        Assert.Equal("f7040000", Convert.ToHexStringLower(dyn.GetBookmark(out _)!));
        Assert.Equal(701, ReadId(dyn));

        // r-cursor-1b/percent-partial-population calls #25..#31: MoveLast -> RecordCount 2048 -> PercentPosition=50 -> AbsolutePosition 1024, ID=1025.
        Assert.Equal(MoveOutcome.Moved, dyn.MoveLast());
        Assert.Equal(2048, dyn.RecordCount);
        Assert.Null(dyn.SetPercentPosition(50f));
        Assert.Equal(1024, dyn.Ordinal);
        Assert.Equal(50f, dyn.PercentPosition(out _));
        Assert.Equal(1025, ReadId(dyn));

        // Requery preserves PK_Big index order.
        Assert.True(dyn.Requery(out _));
        Assert.Equal(MoveOutcome.Moved, dyn.Move(700));
        Assert.Equal(701, ReadId(dyn));

        // r-cursor-1b/percent-partial-population calls #60..#63: Snapshot over same Big table uses storage order (ID=1007 at 50%).
        using PopulationCursor snap = PopulationCursor.Open(source, D, big, pages, forwardOnly: false, out TraversalFailure? snapFail, bookmarkBase: KeysetBookmark.SnapshotBaseForSlot(0), snapshot: true)!;
        Assert.Null(snapFail);
        Assert.Equal(MoveOutcome.Moved, snap.MoveLast());
        Assert.Equal(2048, snap.RecordCount);
        Assert.Null(snap.SetPercentPosition(50f));
        Assert.Equal(1024, snap.Ordinal);
        Assert.Equal(1007, ReadId(snap));
    }

    /// <summary>
    /// Verifies honest <c>NotModelled.DynasetOrder.*</c> refusals on table-opened dynasets with <c>&gt;= 2</c> rows when
    /// an IgnoreNulls PK/index or compound non-PK index is present, and clean tie-break refusal when index names differ
    /// on characters outside ASCII alphanumeric without a host <see cref="IComparer{T}"/> collation.
    /// </summary>
    [Fact]
    public void Dynaset_table_cursor_refuses_unrecorded_index_rules_cleanly_when_two_or_more_rows_exist()
    {
        (PageCache cache, ulong pages, TableDefinition big) = OpenReadOnly(Q2Main, "Big");
        var source = new PageSource(cache);

        // Synthetic IgnoreNulls flag on PK_Big must refuse when >= 2 rows exist.
        PhysicalIndex pk = big.PhysicalIndexes[0];
        var ignoreNullsPk = pk with { Flags = (byte)(pk.Flags | D.IndexDescriptor.IgnoreNullsFlag) };
        TableDefinition ignoreNullsTable = big with { PhysicalIndexes = [ignoreNullsPk] };

        PopulationCursor? refused = PopulationCursor.Open(source, D, ignoreNullsTable, pages, forwardOnly: false, out TraversalFailure? fail, snapshot: false);
        Assert.Null(refused);
        Assert.NotNull(fail);
        Assert.StartsWith("NotModelled.DynasetOrder.IgnoreNullsIndex:", fail.Predicate);

        // Non-PK compound index must refuse cleanly when >= 2 rows exist.
        LogicalIndex nonPk = big.LogicalIndexes[0] with { Name = "IX_Compound", IndexType = 0 };
        PhysicalIndex compoundPhys = pk with
        {
            Flags = 0,
            Columns = [.. pk.Columns, .. pk.Columns],
        };
        TableDefinition compoundTable = big with { LogicalIndexes = [nonPk], PhysicalIndexes = [compoundPhys] };

        PopulationCursor? compoundRefused = PopulationCursor.Open(source, D, compoundTable, pages, forwardOnly: false, out TraversalFailure? compoundFail, snapshot: false);
        Assert.Null(compoundRefused);
        Assert.NotNull(compoundFail);
        Assert.StartsWith("NotModelled.DynasetOrder.CompoundIndex:", compoundFail.Predicate);

        // Single-row table bypasses unrecorded index ambiguity and opens in storage order cleanly.
        using var rCursor1b = new ReplayFixture(RCursor1bMain);
        (TableDefinition singleRowBase, _) = rCursor1b.Table("E")!.Value;
        ColumnDescriptor idCol = singleRowBase.Columns.Single(c => c.Name == "ID");
        ColumnDescriptor textCol = singleRowBase.Columns[1];
        var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = rCursor1b, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
        using (var insCursor = (TableCursor)rCursor1b.Open(singleRowBase, 1)!)
        {
            var frame = new EditFrame(rCursor1b.Table("E")!.Value.Context, insCursor, ws);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(idCol, I4(1)).Succeeded);
            WithBstr("only", v => Assert.True(frame.Assign(textCol, v).Succeeded));
            Assert.True(frame.Update().Succeeded);
        }
        var oneRowIgnoreNulls = singleRowBase with
        {
            LogicalIndexes = [big.LogicalIndexes[0] with { Name = "PK_E", IndexType = D.IndexDescriptor.PrimaryKeyType }],
            PhysicalIndexes = [pk with { Number = 0, Flags = (byte)(pk.Flags | D.IndexDescriptor.IgnoreNullsFlag) }],
        };
        using PopulationCursor oneRowOk = PopulationCursor.Open(new PageSource(rCursor1b.Cache), D, oneRowIgnoreNulls, rCursor1b.Image.PageCount, forwardOnly: false, out TraversalFailure? oneRowFail, snapshot: false)!;
        Assert.Null(oneRowFail);
        Assert.Equal(1, ReadId(oneRowOk));

        // Tied non-PK indexes must refuse NameTieBreakCollation when databaseOrder is null or disagrees with OrdinalIgnoreCase.
        LogicalIndex ixA = big.LogicalIndexes[0] with { Name = "IX", Number = 0, PhysicalNumber = 0, IndexType = 0 };
        LogicalIndex ixB = big.LogicalIndexes[0] with { Name = "IX ", Number = 1, PhysicalNumber = 1, IndexType = 0 };
        PhysicalIndex phys0 = pk with { Number = 0, Flags = D.IndexDescriptor.UniqueFlag };
        PhysicalIndex phys1 = pk with { Number = 1, Flags = D.IndexDescriptor.UniqueFlag };
        TableDefinition tiedTable = big with { LogicalIndexes = [ixA, ixB], PhysicalIndexes = [phys0, phys1] };

        Assert.Null(PopulationCursor.Open(source, D, tiedTable, pages, forwardOnly: false, out TraversalFailure? noCollationFail, snapshot: false));
        Assert.NotNull(noCollationFail);
        Assert.StartsWith("NotModelled.DynasetOrder.NameTieBreakCollation:", noCollationFail.Predicate);

        IComparer<string> trailingSpaceEqual = Comparer<string>.Create(static (x, y) => string.Compare(x?.TrimEnd(), y?.TrimEnd(), StringComparison.OrdinalIgnoreCase));
        Assert.Null(PopulationCursor.Open(source, D, tiedTable, pages, forwardOnly: false, out TraversalFailure? disagreeFail, snapshot: false, databaseOrder: trailingSpaceEqual));
        Assert.NotNull(disagreeFail);
        Assert.StartsWith("NotModelled.DynasetOrder.NameTieBreakCollation:", disagreeFail.Predicate);

        using PopulationCursor agreed = PopulationCursor.Open(source, D, tiedTable, pages, forwardOnly: false, out TraversalFailure? agreedFail, snapshot: false, databaseOrder: StringComparer.OrdinalIgnoreCase)!;
        Assert.Null(agreedFail);
        Assert.Equal(1, ReadId(agreed));

        // IndexNavigator.BeforeDeleted clamps safely when a deleted ordinal exceeds the current leaf count.
        var nav = new IndexNavigator(source, D, pk, big.FirstPage);
        IndexPosition second = nav.Next(nav.First()!.Value)!.Value;
        IndexPosition? clamped = nav.BeforeDeleted(second with { Ordinal = 9999 });
        Assert.NotNull(clamped);
        Assert.Null(nav.LastFailure);
    }

    /// <summary>
    /// evidence/r-str-ado-act/str-edit-requery (#37 chained 6000-char LVAL free/update on recordset Edit/Update)
    /// and evidence/r-q-wire/str-edit-requery-mdb (#18 chained Memo write in MDB): both stay cleanly refused
    /// (<see cref="MutationOpen.Publication"/>) with zero byte changes to the underlying image.
    /// </summary>
    [Fact]
    public void Chained_memo_edit_update_and_mdb_chained_memo_stay_cleanly_refused()
    {
        // 1) ACCDB dynaset Edit/Update freeing or updating a chained Memo stays refused (no kept-page evidence).
        using (var wFixture = new ReplayFixture(WAccdb))
        {
            byte[] before = wFixture.Image.Snapshot();
            string beforeSha = Convert.ToHexStringLower(SHA256.HashData(before));
            (TableDefinition mmTable, TableEditContext mmCtx) = wFixture.Table("Mm")!.Value;
            ColumnDescriptor idCol = mmTable.Columns.Single(c => c.Name == "ID");
            ColumnDescriptor mCol = mmTable.Columns.Single(c => c.Name == "M");

            var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = wFixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
            using var dyn = (PopulationCursor)wFixture.Open(mmTable, 2)!;
            for (dyn.MoveFirst(); dyn.Read(idCol).Value.Integer != 4; dyn.MoveNext())
            {
            }
            var frame = new EditFrame(mmCtx, dyn, ws);
            Assert.True(frame.Edit().Succeeded);
            WithBstr(new string('e', 6001), v => Assert.True(frame.Assign(mCol, v).Succeeded));
            UpdateOutcome refused = frame.Update();
            Assert.False(refused.Succeeded);
            Assert.Equal(MutationOpen.Publication, refused.Open);
            Assert.Equal(beforeSha, Convert.ToHexStringLower(SHA256.HashData(wFixture.Image.Snapshot())));
        }

        // 2) MDB chained Memo write via Edit/AddNew stays cleanly refused (RowWriter: "chained Memo long value in an MDB file").
        using (var mdbFixture = new ReplayFixture(StrMdb))
        {
            byte[] before = mdbFixture.Image.Snapshot();
            string beforeSha = Convert.ToHexStringLower(SHA256.HashData(before));
            (TableDefinition sTable, TableEditContext sCtx) = mdbFixture.Table("S")!.Value;
            ColumnDescriptor idCol = sTable.Columns.Single(c => c.Name == "ID");
            ColumnDescriptor mCol = sTable.Columns.Single(c => c.Name == "M");

            var ws = new TransactionCoordinator(new LockerId(1)) { Publisher = mdbFixture, Scheduler = new CountingScheduler(), Clock = static () => TimeSpan.Zero };
            using var cursor = (TableCursor)mdbFixture.Open(sTable, 1)!;
            var frame = new EditFrame(sCtx, cursor, ws);
            Assert.True(frame.AddNew().Succeeded);
            Assert.True(frame.Assign(idCol, I4(999)).Succeeded);
            WithBstr(new string('e', 6001), v => Assert.True(frame.Assign(mCol, v).Succeeded));
            UpdateOutcome refused = frame.Update();
            Assert.False(refused.Succeeded);
            Assert.Equal(MutationOpen.Publication, refused.Open);
            Assert.Contains("MDB", refused.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeSha, Convert.ToHexStringLower(SHA256.HashData(mdbFixture.Image.Snapshot())));
        }
    }
}
