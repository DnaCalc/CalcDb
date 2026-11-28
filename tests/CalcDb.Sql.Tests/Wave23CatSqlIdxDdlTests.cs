using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Isam.Transactions;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Wave 23 catalog/DDL index build validation and SQL/Expressions follow-ups
/// (W-06, W-07, R-CAT-1, R-SQL-1, R-SORT-3).
/// </summary>
public sealed class Wave23CatSqlIdxDdlTests
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string RqCursorDaoMainSha = "37cb1596da09f7b6ba43f90571a8d381a057852eb7d762c73044c0a839134012";
    private const string Rq2CursorDaoMainSha = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2";
    private const string Rq2AdoMiscMainSha = "dfe5bf3ce6e08aeb6e9f3b4edb71b33feda26e2cd4b1380709b9bfb6ef642c5b";
    private const string Rado1bMainSha = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";

    private const string Sort3bMixSha = "b2ce06328efc99cd7f9d422ec16f173656f75b0a23dce96912884ede2cd8fe51";
    private const string Sort3bLgSha = "a9f42cfc94a8907aaa00c139401d8f1a6b5e0619e284de100e3dbbfd9610e629";
    private const string Sort3bMemoSha = "75c5396af6bc118572f97337ae8f3033df537b73c2da936c866ecb52164afd6c";
    private const string Sort3KeysAccdbSha = "1969595a7bf160ed36ce7ce610a78a5ae4385f66a945e1fb57cbd0346eeab7e1";
    private const string Sort3KeysMdbSha = "c5f0617ab966c6d48ccfb8043bfff93c513b43cbc663b3d1a8f4b85e0a101919";
    private const string Sort3CorpusAccdbSha = "433b610ae464f91cac0b1c0fe1b261b3db57b1e01e0177cd2045591ead422f3a";
    private const string Sort3CorpusMdbSha = "3e2357003d810aa2065a6ce8ab49034dae83b23885abdeeacca3619087bccc0c";
    private const string Rq3SqlMainSha = "f1d52fdbd7e5ea6db7b891d3a571671745b3c00a85f26b36df01a1cc5f7828c9";

    [Theory]
    [InlineData(RqCursorDaoMainSha, "Ib1", "UxA", "UxC", true)]
    [InlineData(RqCursorDaoMainSha, "Ib2", "UxB", "UxC", true)]
    [InlineData(Rq2CursorDaoMainSha, "IbA", "UxA", "UxC", false)]
    [InlineData(Rq2CursorDaoMainSha, "IbB", "UxB", "UxC", false)]
    public void CreateIndex_disallow_null_3058_precedes_duplicate_3022_regardless_of_row_or_key_order(
        string fixtureSha,
        string table,
        string disallowNullIndex,
        string duplicateIndex,
        bool hasCleanTailExtent)
    {
        // evidence/r-q-cursor-dao/index-build-order-failure (calls #10, #20 -> 3058; #30 -> 3022) &
        // evidence/r-q2-cursor-dao/index-null-sorts-after-duplicate (call #11 -> 3058 even when Null key sorts after duplicate).
        using (var daoDb = new WritableSqlFixture(fixtureSha))
        {
            SchemaWriter writer = daoDb.OpenSchemaWriter(out string? err)!;
            Assert.Null(err);
            var req = new CreateIndexRequest(
                table,
                new NewIndex(disallowNullIndex, ["A", "B"], PrimaryKey: false, Unique: true, DisallowNull: true),
                SchemaRoute.Dao);
            if (hasCleanTailExtent)
            {
                SchemaOutcome daoDisallow = writer.CreateIndex(daoDb.Workspace, req, daoDb.NowDateBits());
                Assert.Equal(3058, daoDisallow.Refused?.Error);
            }
            else
            {
                // r-q2-cursor-dao-main.accdb has an interior free page (125 < 192: R-CRASH-1 CRASH-ORDER Open)
                // preventing 32-page tail reservation; acceptsError probe still verifies Null (3058) wins over 3022.
                SchemaOutcome probed = writer.CreateIndex(daoDb.Workspace, req, daoDb.NowDateBits(), acceptsError: _ => false);
                Assert.Equal(3058, probed.Refused?.Error);
            }
        }

        using var sqlDb = new WritableSqlFixture(fixtureSha);
        byte[] startBytes = sqlDb.Image.Snapshot();

        // Fresh-session 3058 page bytes on DISALLOW NULL remain unrecorded (SchemaWriterTests PK_C / IX_C_DIS contract),
        // so BuildIndexLeaf detects Null first (3058) and refuses cleanly with NotModelled.SchemaAccept.Publication rather than 3022.
        ActionOutcome freshDisallow = ActionExecutor.Execute(
            $"CREATE UNIQUE INDEX {disallowNullIndex} ON {table} (A, B) WITH DISALLOW NULL",
            sqlDb);
        Assert.False(freshDisallow.Succeeded);
        Assert.Equal("NotModelled.SchemaAccept.Publication", freshDisallow.Failure?.NotModelled);
        Assert.Contains("existing Null row in DISALLOW NULL index", freshDisallow.Failure?.Message, StringComparison.Ordinal);
        Assert.Equal(startBytes, sqlDb.Image.Snapshot());

        if (hasCleanTailExtent)
        {
            // Without DISALLOW NULL, duplicate keys fail 3022 and reserve the 32-page session extent;
            // a subsequent DISALLOW NULL attempt in that session then returns ACE 3058.
            ActionOutcome dupOutcome = ActionExecutor.Execute(
                $"CREATE UNIQUE INDEX {duplicateIndex} ON {table} (A, B)",
                sqlDb);
            Assert.False(dupOutcome.Succeeded);
            Assert.False(dupOutcome.Failure?.IsNotModelled);
            Assert.Equal(3022, dupOutcome.Failure?.Number);

            ActionOutcome postResDisallow = ActionExecutor.Execute(
                $"CREATE UNIQUE INDEX {disallowNullIndex} ON {table} (A, B) WITH DISALLOW NULL",
                sqlDb);
            Assert.False(postResDisallow.Succeeded);
            Assert.False(postResDisallow.Failure?.IsNotModelled);
            Assert.Equal(3058, postResDisallow.Failure?.Number);
        }
    }

    [Fact]
    public void CreateIndex_null_sorting_after_duplicate_returns_3058_with_full_rollback_on_clean_db()
    {
        // Clean accdb reproduction of r-q2-cursor-dao/index-null-sorts-after-duplicate (IbA: duplicate (1,1) before Null (9,Null)):
        using var setupDb = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE IbA (A LONG, B LONG)", setupDb).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO IbA (A, B) VALUES (1, 1)", setupDb).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO IbA (A, B) VALUES (1, 1)", setupDb).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO IbA (A, B) VALUES (9, NULL)", setupDb).Succeeded);
        byte[] cleanBytes = setupDb.Image.Snapshot();

        using var daoDb = new WritableSqlFixture(cleanBytes);
        SchemaWriter writer = daoDb.OpenSchemaWriter(out _) !;
        SchemaOutcome daoOutcome = writer.CreateIndex(
            daoDb.Workspace,
            new CreateIndexRequest("IbA", new NewIndex("UxA", ["A", "B"], PrimaryKey: false, Unique: true, DisallowNull: true), SchemaRoute.Dao),
            daoDb.NowDateBits());
        Assert.Equal(3058, daoOutcome.Refused?.Error);
    }

    [Fact]
    public void CreateIndex_unique_text_admits_existing_nulls_and_keeps_unrecorded_post_ddl_3022_open()
    {
        // evidence/r-fix-fmt-sort/sort3b-existing-nulls-isolated (E_uk: CREATE UNIQUE INDEX UX ON E_uk (K) on TEXT(20), 7 rows from BK -> DistinctCount = 5)
        using (var db = new WritableSqlFixture(EmptyAccdbSha))
        {
            Assert.True(ActionExecutor.Execute("CREATE TABLE E_uk (K TEXT(20), N LONG)", db).Succeeded);
            foreach (string sql in (string[])
                [
                    "INSERT INTO E_uk (K, N) VALUES (NULL, 1)",
                    "INSERT INTO E_uk (K, N) VALUES (NULL, 2)",
                    "INSERT INTO E_uk (K, N) VALUES (NULL, 3)",
                    "INSERT INTO E_uk (K, N) VALUES ('a', 4)",
                    "INSERT INTO E_uk (K, N) VALUES ('b', 5)",
                    "INSERT INTO E_uk (K, N) VALUES ('', 6)",
                    "INSERT INTO E_uk (K, N) VALUES ('c', 7)",
                ])
            {
                Assert.True(ActionExecutor.Execute(sql, db).Succeeded);
            }

            ActionOutcome ux = ActionExecutor.Execute("CREATE UNIQUE INDEX UX ON E_uk (K)", db);
            Assert.True(ux.Succeeded, ux.Failure?.ToString());

            TableDefinition eUk = db.Table("E_uk")!;
            LogicalIndex logUx = Assert.Single(eUk.LogicalIndexes);
            Assert.Equal("UX", logUx.Name);
            PhysicalIndex physUx = Assert.Single(eUk.PhysicalIndexes);
            Assert.True(physUx.Unique(FormatDescriptor.Jet4Family.IndexDescriptor));
            Assert.Equal(5u, physUx.UniqueEntryCount);
            Assert.Equal(7u, physUx.EntryCount);
        }

        // evidence/r-sort-3b/index-on-existing-nulls (call #46 CREATE UNIQUE INDEX UX1 ON X (K) -> 3022):
        // BuildIndexLeaf now scans past Null Text rows to hit the duplicate key (3022), while post-DDL
        // 0-byte 3022 in a single-table CREATE+INSERT session stays refused as NotModelled.SchemaAccept.Publication.
        using (var dupDb = new WritableSqlFixture(EmptyAccdbSha))
        {
            Assert.True(ActionExecutor.Execute("CREATE TABLE X (K TEXT(20), N LONG)", dupDb).Succeeded);
            foreach (string sql in (string[])
                [
                    "INSERT INTO X (K, N) VALUES (NULL, 1)",
                    "INSERT INTO X (K, N) VALUES ('dup', 2)",
                    "INSERT INTO X (K, N) VALUES (NULL, 3)",
                    "INSERT INTO X (K, N) VALUES ('dup', 4)",
                ])
            {
                Assert.True(ActionExecutor.Execute(sql, dupDb).Succeeded);
            }

            byte[] before = dupDb.Image.Snapshot();
            ActionOutcome dupFail = ActionExecutor.Execute("CREATE UNIQUE INDEX UX1 ON X (K)", dupDb);
            Assert.False(dupFail.Succeeded);
            Assert.Equal("NotModelled.SchemaAccept.Publication", dupFail.Failure?.NotModelled);
            Assert.Contains("3022", dupFail.Failure?.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("UNIQUE 0x0A Null keys is not recorded", dupFail.Failure?.Message, StringComparison.Ordinal);
            Assert.Equal(before, dupDb.Image.Snapshot());
        }
    }

    [Fact]
    public void CreateIndex_dao_ole_field_refuses_3409_and_memo_stays_unrecorded()
    {
        // evidence/r-q-cursor-dao/index-nonindexable-types call #31 (ixs.Append(IxDaoO) -> 3409) and call #47 (IxDaoMm -> open).
        using var db = new WritableSqlFixture(RqCursorDaoMainSha);
        byte[] startBytes = db.Image.Snapshot();
        SchemaWriter writer = db.OpenSchemaWriter(out string? err)!;
        Assert.Null(err);

        SchemaOutcome oleOutcome = writer.CreateIndex(
            db.Workspace,
            new CreateIndexRequest("Ob", new NewIndex("IxDaoO", ["O"], PrimaryKey: false), SchemaRoute.Dao),
            db.NowDateBits());
        Assert.Equal(new SchemaRefusal(3409, "O"), oleOutcome.Refused);
        Assert.False(oleOutcome.PublishedAny);
        Assert.Equal(startBytes, db.Image.Snapshot());

        SchemaOutcome memoOutcome = writer.CreateIndex(
            db.Workspace,
            new CreateIndexRequest("Ob", new NewIndex("IxDaoMm", ["Mm"], PrimaryKey: false), SchemaRoute.Dao),
            db.NowDateBits());
        Assert.NotNull(memoOutcome.Refused);
        Assert.Null(memoOutcome.Refused.Value.Error);
        Assert.False(memoOutcome.PublishedAny);
        Assert.Equal(startBytes, db.Image.Snapshot());
    }

    [Fact]
    public void Update_unknown_unqualified_set_column_fails_3061_while_qualified_stays_open()
    {
        // evidence/r-q2-ado-misc/ado-recordsaffected-byref calls #110, #117:
        // UPDATE K SET NoSuchColumn = 1 -> 0x80040E10 "No value given for one or more required parameters." (ACE 3061).
        using var db = new WritableSqlFixture(Rq2AdoMiscMainSha);
        byte[] startBytes = db.Image.Snapshot();

        ActionOutcome outcome = ActionExecutor.Execute("UPDATE K SET NoSuchColumn = 1", db, options: 1);
        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Failure?.IsNotModelled);
        Assert.Equal(3061, outcome.Failure?.Number);
        Assert.Equal("Too few parameters. Expected 1.", outcome.Failure?.Message);
        Assert.Equal(startBytes, db.Image.Snapshot());

        ActionOutcome qualified = ActionExecutor.Execute("UPDATE K SET K.NoSuchColumn = 1", db, options: 1);
        Assert.False(qualified.Succeeded);
        Assert.Equal("NotModelled.UnknownUpdateColumn", qualified.Failure?.NotModelled);

        ActionOutcome multi = ActionExecutor.Execute("UPDATE K SET V = 1, NoSuchColumn = 1", db, options: 1);
        Assert.False(multi.Succeeded);
        Assert.Equal("NotModelled.UnknownUpdateColumn", multi.Failure?.NotModelled);
        Assert.Equal(startBytes, db.Image.Snapshot());
    }

    [Fact]
    public void Fields_meta_expressions_cdec_single_arg_fails_3075_and_now_stays_open()
    {
        // evidence/r-ado-1b/fields-meta-expressions calls #83/#100 (CDec(L) -> 3075) and #15 (Now() -> open clock).
        var db = new SqlFixture(Rado1bMainSha);

        using BoundSelect? cdecPlan = BoundSelect.Prepare("SELECT CDec(L) AS x1 FROM T WHERE ID = 1", db, out SqlFailure? cdecFailure);
        Assert.Null(cdecPlan);
        Assert.NotNull(cdecFailure);
        Assert.False(cdecFailure!.IsNotModelled);
        Assert.Equal(3075, cdecFailure.Number);
        Assert.Equal("Wrong number of arguments used with function in query expression 'CDec(L'.", cdecFailure.Message);

        using BoundSelect? nowPlan = BoundSelect.Prepare(
            "SELECT L + 1 AS e1, Now() AS e18 FROM T WHERE ID = 1",
            db,
            out SqlFailure? nowFailure);
        Assert.Null(nowPlan);
        Assert.NotNull(nowFailure);
        Assert.Equal("NotModelled.Function", nowFailure!.NotModelled);
    }

    public static TheoryData<string, string, string[]> RowOrderSnapshotStopCases() => new()
    {
        { "r-sort-3b/mix-order-b", Sort3bMixSha, ["SELECT ID FROM MB ORDER BY K, V1", "SELECT ID FROM MB ORDER BY V1, K", "SELECT ID FROM MB ORDER BY K DESC, V1", "SELECT ID FROM MB ORDER BY K, V1 DESC", "SELECT ID FROM MB ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/mix-order-c", Sort3bMixSha, ["SELECT ID FROM MC ORDER BY K, V1", "SELECT ID FROM MC ORDER BY V1, K", "SELECT ID FROM MC ORDER BY K DESC, V1", "SELECT ID FROM MC ORDER BY K, V1 DESC", "SELECT ID FROM MC ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/mix-order-d", Sort3bMixSha, ["SELECT ID FROM MD ORDER BY K, V1", "SELECT ID FROM MD ORDER BY V1, K", "SELECT ID FROM MD ORDER BY K DESC, V1", "SELECT ID FROM MD ORDER BY K, V1 DESC", "SELECT ID FROM MD ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/mix-order-n", Sort3bMixSha, ["SELECT ID FROM MN ORDER BY K, V1", "SELECT ID FROM MN ORDER BY V1, K", "SELECT ID FROM MN ORDER BY K DESC, V1", "SELECT ID FROM MN ORDER BY K, V1 DESC", "SELECT ID FROM MN ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/mix-order-r", Sort3bMixSha, ["SELECT ID FROM MR ORDER BY K, V1", "SELECT ID FROM MR ORDER BY V1, K", "SELECT ID FROM MR ORDER BY K DESC, V1", "SELECT ID FROM MR ORDER BY K, V1 DESC", "SELECT ID FROM MR ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/mix-order-y", Sort3bMixSha, ["SELECT ID FROM MY ORDER BY K, V1", "SELECT ID FROM MY ORDER BY V1, K", "SELECT ID FROM MY ORDER BY K DESC, V1", "SELECT ID FROM MY ORDER BY K, V1 DESC", "SELECT ID FROM MY ORDER BY K DESC, V1 DESC"] },
        { "r-sort-3b/lg-sql-order", Sort3bLgSha, ["SELECT ID FROM T ORDER BY K", "SELECT ID FROM T ORDER BY K DESC", "SELECT TOP 5 ID FROM T ORDER BY K", "SELECT TOP 5 ID FROM T ORDER BY K DESC", "SELECT ID FROM T0 ORDER BY K", "SELECT ID FROM T0 ORDER BY K DESC"] },
        { "r-sort-3b/memo-order-sql", Sort3bMemoSha, ["SELECT ID FROM MM ORDER BY MK", "SELECT ID FROM MM ORDER BY MK DESC", "SELECT ID FROM MM ORDER BY TK", "SELECT ID FROM MM ORDER BY TK DESC"] },
        { "r-sort-3/keys-order-sql-accdb", Sort3KeysAccdbSha, ["SELECT ID FROM LK ORDER BY K, N", "SELECT ID FROM LK ORDER BY K DESC, N DESC", "SELECT ID FROM FR ORDER BY K, N"] },
        { "r-sort-3/keys-order-sql-mdb", Sort3KeysMdbSha, ["SELECT ID FROM LK ORDER BY K, N", "SELECT ID FROM LK ORDER BY K DESC, N DESC", "SELECT ID FROM FR ORDER BY K, N"] },
        { "r-sort-3/order-sql-accdb", Sort3CorpusAccdbSha, ["SELECT ID FROM T ORDER BY K", "SELECT ID FROM T ORDER BY K DESC", "SELECT ID FROM T2 ORDER BY K", "SELECT ID FROM U ORDER BY K", "SELECT ID FROM U ORDER BY K DESC"] },
        { "r-sort-3/order-sql-mdb", Sort3CorpusMdbSha, ["SELECT ID FROM T ORDER BY K", "SELECT ID FROM T ORDER BY K DESC", "SELECT ID FROM T2 ORDER BY K", "SELECT ID FROM U ORDER BY K", "SELECT ID FROM U ORDER BY K DESC"] },
        { "r-q3-sql/ties-t50", Rq3SqlMainSha, ["SELECT ID, K FROM T50 ORDER BY K", "SELECT ID, K FROM T50 ORDER BY K DESC"] },
        { "r-q3-sql/ties-t200", Rq3SqlMainSha, ["SELECT ID, K FROM T200 ORDER BY K", "SELECT ID, K FROM T200 ORDER BY K DESC"] },
        { "r-q3-sql/ties-t1000", Rq3SqlMainSha, ["SELECT ID, K FROM T1000 ORDER BY K", "SELECT ID, K FROM T1000 ORDER BY K DESC"] },
    };

    [Theory]
    [MemberData(nameof(RowOrderSnapshotStopCases))]
    public void Snapshot_sort_ties_over_9_rows_stop_at_row_order_on_cursor_value_read(
        string question,
        string fixtureSha,
        string[] sqls)
    {
        // compare-stops wave22-1: all 15 Snapshot tie queries stop cleanly at SelectCursor.TieRule (NotModelled.RowOrder),
        // which SelectRecordset.cs propagates as COM 0x80020009 during Field.Value reads; tie permutation remains open.
        var db = new SqlFixture(fixtureSha);
        foreach (string sql in sqls)
        {
            using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? bindFail);
            Assert.True(bindFail is null, $"{question} '{sql}': {bindFail}");
            using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, null, out SqlFailure? openFail);
            Assert.True(openFail is null, $"{question} '{sql}': {openFail}");
            Assert.NotNull(cursor);

            bool sawRowOrder = false;
            while (!cursor!.Eof)
            {
                if (!cursor.Read(0, out _, out SqlFailure? readFail))
                {
                    Assert.Equal("NotModelled.RowOrder", readFail?.NotModelled);
                    sawRowOrder = true;
                    break;
                }
                cursor.MoveNext();
            }
            Assert.True(sawRowOrder, $"{question} '{sql}': expected NotModelled.RowOrder");
        }
    }
}
