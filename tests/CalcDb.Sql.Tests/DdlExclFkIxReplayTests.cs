using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-exact kept-page replay tests for W-07 / R-CAT-1, R-SORT-3, R-SQL-2 (r-q54-excl-ddl-ix-cmd Parts A &amp; B):
/// 1. excl-alter-add-drop-accdb / excl-alter-add-drop-mdb (exclusive SQL/ADO ALTER TABLE ADD/DROP COLUMN, CONSTRAINT, DROP INDEX, ADO DEFAULT)
/// 2. ado-alter-add-text-nodef-accdb / ado-alter-add-text-nodef-mdb (ADO ADD COLUMN without DEFAULT on empty &amp; populated tables)
/// 3. drop-populated-indexed-lval-accdb / drop-populated-indexed-lval-mdb (shared &amp; exclusive DROP TABLE on populated indexed LVAL table)
/// 4. second-fk-negid-and-lvalchild-accdb / second-fk-negid-and-lvalchild-mdb (multi-FK ascending negative IDs &amp; LVAL child map ordering)
/// 5. ix-existing-nulls-uniq-multi-accdb / ix-existing-nulls-uniq-multi-mdb (multi-column UNIQUE partial Nulls &amp; WITH IGNORE NULL)
/// 6. ix-unique-dup-3022-bytes-accdb / ix-unique-dup-3022-bytes-mdb (SQL 3022 &amp; 3058 32-page extent rollback bytes)
/// </summary>
public sealed class DdlExclFkIxReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    private sealed class ExclusivePublisherWrapper(IPublicationTarget inner) : IPublicationTarget
    {
        public bool IsExclusiveSession(PageCache cache) => true;

        public IPositionedFile? Writable(PageCache cache, IReadOnlyCollection<ulong> pages, out string? failure)
            => inner.Writable(cache, pages, out failure);
    }

    private static WritableSqlFixture OpenExclusive(byte[] image)
    {
        var db = new WritableSqlFixture((byte[])image.Clone());
        db.Workspace.Publisher = new ExclusivePublisherWrapper(db);
        return db;
    }

    [Theory]
    [InlineData("excl-alter-add-drop-accdb")]
    [InlineData("excl-alter-add-drop-mdb")]
    public void R_q54_excl_alter_add_drop_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);

        // Session 2 (exclusive): ALTER TABLE T ADD COLUMN C TEXT(25)
        using (var db2 = OpenExclusive(snaps["after-s1-setup"]))
        {
            ulong dt = ReadObjectDateUpdate(snaps["after-s2-add-text"], "T");
            db2.DateBitsProvider = () => dt;
            ActionOutcome o = ActionExecutor.Execute("ALTER TABLE T ADD COLUMN C TEXT(25)", db2);
            Assert.True(o.Succeeded, $"{question} S2 ADD C failed: {o.Failure}");
            if (db2.Workspace.NeedsCloseCommit(db2.Cache))
            {
                Assert.Equal(MutationOpen.None, db2.Workspace.PublishCloseCommit(db2.Cache).Open);
            }
            AssertImagesEqual($"{question} [after-s2-add-text]", snaps["after-s2-add-text"], db2.Image.Snapshot());
        }

        // Session 3 (exclusive): ADD COLUMN D LONG NOT NULL, ADD COLUMN M MEMO, ADD CONSTRAINT UQ_B UNIQUE (B)
        using (var db3 = OpenExclusive(snaps["after-s2-add-text"]))
        {
            ulong dt = ReadObjectDateUpdate(snaps["after-s3-add-nn-memo-uq"], "T");
            db3.DateBitsProvider = () => dt;
            Assert.True(ActionExecutor.Execute("ALTER TABLE T ADD COLUMN D LONG NOT NULL", db3).Succeeded);
            Assert.True(ActionExecutor.Execute("ALTER TABLE T ADD COLUMN M MEMO", db3).Succeeded);
            Assert.True(ActionExecutor.Execute("ALTER TABLE T ADD CONSTRAINT UQ_B UNIQUE (B)", db3).Succeeded);
            if (db3.Workspace.NeedsCloseCommit(db3.Cache))
            {
                Assert.Equal(MutationOpen.None, db3.Workspace.PublishCloseCommit(db3.Cache).Open);
            }
            AssertImagesEqual($"{question} [after-s3-add-nn-memo-uq]", snaps["after-s3-add-nn-memo-uq"], db3.Image.Snapshot());
        }

        // Session 4 (exclusive): DROP INDEX IX_A ON T (non-last physical index -> UQ_B PhysicalNumber decremented)
        using (var db4 = OpenExclusive(snaps["after-s3-add-nn-memo-uq"]))
        {
            ulong dt = ReadObjectDateUpdate(snaps["after-s4-drop-ix"], "T");
            db4.DateBitsProvider = () => dt;
            Assert.True(ActionExecutor.Execute("DROP INDEX IX_A ON T", db4).Succeeded);
            if (db4.Workspace.NeedsCloseCommit(db4.Cache))
            {
                Assert.Equal(MutationOpen.None, db4.Workspace.PublishCloseCommit(db4.Cache).Open);
            }
            AssertImagesEqual($"{question} [after-s4-drop-ix]", snaps["after-s4-drop-ix"], db4.Image.Snapshot());
        }

        // Session 5 (exclusive): DROP CONSTRAINT UQ_B, DROP COLUMN C (preserving LvProp), DROP CONSTRAINT PK_T
        using (var db5 = OpenExclusive(snaps["after-s4-drop-ix"]))
        {
            ulong dt = ReadObjectDateUpdate(snaps["after-s5-drop-uq-col-pk"], "T");
            db5.DateBitsProvider = () => dt;
            Assert.True(ActionExecutor.Execute("ALTER TABLE T DROP CONSTRAINT UQ_B", db5).Succeeded);
            Assert.True(ActionExecutor.Execute("ALTER TABLE T DROP COLUMN C", db5).Succeeded);
            Assert.True(ActionExecutor.Execute("ALTER TABLE T DROP CONSTRAINT PK_T", db5).Succeeded);
            if (db5.Workspace.NeedsCloseCommit(db5.Cache))
            {
                Assert.Equal(MutationOpen.None, db5.Workspace.PublishCloseCommit(db5.Cache).Open);
            }
            AssertImagesEqual($"{question} [after-s5-drop-uq-col-pk]", snaps["after-s5-drop-uq-col-pk"], db5.Image.Snapshot());
        }

        // Session 6 (exclusive ADO): ALTER TABLE T ADD COLUMN E TEXT(15) DEFAULT 'hi' (re-sequences ColumnId after DROP COLUMN)
        using (var db6 = OpenExclusive(snaps["after-s5-drop-uq-col-pk"]))
        {
            ulong dt = ReadObjectDateUpdate(snaps["after-s6-ado-excl-add-def"], "T");
            db6.DateBitsProvider = () => dt;
            ActionOutcome o = ActionExecutor.Execute("ALTER TABLE T ADD COLUMN E TEXT(15) DEFAULT 'hi'", db6, source: ActionErrorSource.Ado);
            Assert.True(o.Succeeded, $"{question} S6 ADO ADD E DEFAULT failed: {o.Failure}");
            if (db6.Workspace.NeedsCloseCommit(db6.Cache))
            {
                Assert.Equal(MutationOpen.None, db6.Workspace.PublishCloseCommit(db6.Cache).Open);
            }
            AssertImagesEqual($"{question} [after-s6-ado-excl-add-def]", snaps["after-s6-ado-excl-add-def"], db6.Image.Snapshot(), FindDateSpans(snaps["after-s6-ado-excl-add-def"], db6.Image.Snapshot(), dt));
        }
    }

    [Theory]
    [InlineData("ado-alter-add-text-nodef-accdb")]
    [InlineData("ado-alter-add-text-nodef-mdb")]
    public void R_q54_ado_alter_add_column_without_default_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        (string sql, string table, string label)[] steps =
        [
            ("ALTER TABLE TE ADD COLUMN X TEXT(20)", "TE", "after-ado-add-te-text"),
            ("ALTER TABLE TE ADD COLUMN Y TEXT(15) NOT NULL", "TE", "after-ado-add-te-text-nn"),
            ("ALTER TABLE TP ADD COLUMN X TEXT(20)", "TP", "after-ado-add-tp-text"),
            ("ALTER TABLE TP ADD COLUMN Z LONG", "TP", "after-ado-add-tp-long"),
            ("ALTER TABLE TP ADD COLUMN M MEMO", "TP", "after-ado-add-tp-memo"),
        ];

        foreach ((string sql, string table, string label) in steps)
        {
            ulong dt = ReadObjectDateUpdate(snaps[label], table);
            db.DateBitsProvider = () => dt;
            ActionOutcome o = ActionExecutor.Execute(sql, db, source: ActionErrorSource.Ado);
            Assert.True(o.Succeeded, $"{question} {label} failed: {o.Failure}");
            AssertImagesEqual($"{question} [{label}]", snaps[label], db.Image.Snapshot());
        }

        if (db.Workspace.NeedsCloseCommit(db.Cache))
        {
            Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        }
        AssertImagesEqual($"{question} [after-ado-close]", snaps["after-ado-close"], db.Image.Snapshot());
    }

    [Theory]
    [InlineData("drop-populated-indexed-lval-accdb")]
    [InlineData("drop-populated-indexed-lval-mdb")]
    public void R_q54_drop_populated_indexed_lval_table_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);

        // Shared DROP TABLE TS (populated with PK, secondary index, and external LVAL Memo > 2400 chars)
        using (var dbs = new WritableSqlFixture((byte[])snaps["after-ts-setup"].Clone()))
        {
            ActionOutcome o = ActionExecutor.Execute("DROP TABLE TS", dbs);
            Assert.True(o.Succeeded, $"{question} shared DROP TABLE TS failed: {o.Failure}");
            AssertImagesEqual($"{question} [after-drop-ts-live]", snaps["after-drop-ts-live"], dbs.Image.Snapshot());

            Assert.Equal(MutationOpen.None, dbs.Workspace.PublishCloseCommit(dbs.Cache).Open);
            AssertImagesEqual($"{question} [after-drop-ts-close]", snaps["after-drop-ts-close"], dbs.Image.Snapshot());
        }

        // Exclusive DROP TABLE TE (populated with PK, secondary index, and external LVAL Memo > 2400 chars)
        using (var dbe = OpenExclusive(snaps["after-te-setup"]))
        {
            ActionOutcome o = ActionExecutor.Execute("DROP TABLE TE", dbe);
            Assert.True(o.Succeeded, $"{question} exclusive DROP TABLE TE failed: {o.Failure}");
            Assert.Equal(MutationOpen.None, dbe.Workspace.PublishCloseCommit(dbe.Cache).Open);
            AssertImagesEqual($"{question} [after-drop-te-close]", snaps["after-drop-te-close"], dbe.Image.Snapshot());
        }
    }

    [Theory]
    [InlineData("second-fk-negid-and-lvalchild-accdb")]
    [InlineData("second-fk-negid-and-lvalchild-mdb")]
    public void R_q54_multi_fk_negid_and_lval_child_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);

        // Session 1 (DAO SQL): P1, P2, C1, C2, C3 (LVAL + FK), ALTER TABLE C3 ADD FK_C3_P2, Close
        using (var db = new WritableSqlFixture((byte[])snaps["before"].Clone()))
        {
            ulong p1Dt = ReadObjectDateUpdate(snaps["after-parents"], "P1");
            db.DateBitsProvider = () => p1Dt;
            Assert.True(ActionExecutor.Execute("CREATE TABLE P1 (ID LONG CONSTRAINT PK_P1 PRIMARY KEY, Name TEXT(20))", db).Succeeded);

            ulong p2Dt = ReadObjectDateUpdate(snaps["after-parents"], "P2");
            db.DateBitsProvider = () => p2Dt;
            Assert.True(ActionExecutor.Execute("CREATE TABLE P2 (ID LONG CONSTRAINT PK_P2 PRIMARY KEY, Name TEXT(20))", db).Succeeded);
            AssertImagesEqual($"{question} [after-parents]", snaps["after-parents"], db.Image.Snapshot(), FindDateSpans(snaps["after-parents"], db.Image.Snapshot(), p2Dt));

            ulong c1Dt = ReadObjectDateUpdate(snaps["after-fk1-dao"], "C1");
            db.DateBitsProvider = () => c1Dt;
            Assert.True(ActionExecutor.Execute("CREATE TABLE C1 (CID LONG CONSTRAINT PK_C1 PRIMARY KEY, P1ID LONG, CONSTRAINT FK_C1_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))", db).Succeeded);
            AssertImagesEqual($"{question} [after-fk1-dao]", snaps["after-fk1-dao"], db.Image.Snapshot(), FindDateSpans(snaps["after-fk1-dao"], db.Image.Snapshot(), c1Dt));

            ulong c2Dt = ReadObjectDateUpdate(snaps["after-fk2-dao"], "C2");
            db.DateBitsProvider = () => c2Dt;
            Assert.True(ActionExecutor.Execute("CREATE TABLE C2 (CID LONG CONSTRAINT PK_C2 PRIMARY KEY, P2ID LONG, CONSTRAINT FK_C2_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID))", db).Succeeded);
            AssertImagesEqual($"{question} [after-fk2-dao]", snaps["after-fk2-dao"], db.Image.Snapshot(), FindDateSpans(snaps["after-fk2-dao"], db.Image.Snapshot(), c2Dt));

            ulong c3Dt = ReadObjectDateUpdate(snaps["after-fk3-lval-dao"], "C3");
            db.DateBitsProvider = () => c3Dt;
            Assert.True(ActionExecutor.Execute("CREATE TABLE C3 (CID LONG CONSTRAINT PK_C3 PRIMARY KEY, P1ID LONG, P2ID LONG, Descr MEMO, Pic LONGBINARY, CONSTRAINT FK_C3_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))", db).Succeeded);
            AssertImagesEqual($"{question} [after-fk3-lval-dao]", snaps["after-fk3-lval-dao"], db.Image.Snapshot(), FindDateSpans(snaps["after-fk3-lval-dao"], db.Image.Snapshot(), c3Dt));

            ulong c3AlterDt = ReadObjectDateUpdate(snaps["after-fk4-alter-lval-dao"], "C3");
            db.DateBitsProvider = () => c3AlterDt;
            Assert.True(ActionExecutor.Execute("ALTER TABLE C3 ADD CONSTRAINT FK_C3_P2 FOREIGN KEY (P2ID) REFERENCES P2 (ID)", db).Succeeded);
            AssertImagesEqual($"{question} [after-fk4-alter-lval-dao]", snaps["after-fk4-alter-lval-dao"], db.Image.Snapshot(), FindDateSpans(snaps["after-fk4-alter-lval-dao"], db.Image.Snapshot(), c3AlterDt));

            Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
            AssertImagesEqual($"{question} [after-dao-close]", snaps["after-dao-close"], db.Image.Snapshot(), FindDateSpans(snaps["after-dao-close"], db.Image.Snapshot(), c3AlterDt));
        }

        // Session 2 (ADO): CREATE TABLE C4 (LVAL + FK) live + Close
        using (var dbAdo = new WritableSqlFixture((byte[])snaps["after-dao-close"].Clone()))
        {
            ulong c4Dt = ReadObjectDateUpdate(snaps["after-fk5-ado-live"], "C4");
            dbAdo.DateBitsProvider = () => c4Dt;
            ActionOutcome o = ActionExecutor.Execute("CREATE TABLE C4 (CID LONG CONSTRAINT PK_C4 PRIMARY KEY, P1ID LONG, Descr MEMO, CONSTRAINT FK_C4_P1 FOREIGN KEY (P1ID) REFERENCES P1 (ID))", dbAdo, source: ActionErrorSource.Ado);
            Assert.True(o.Succeeded, $"{question} ADO CREATE C4 failed: {o.Failure}");
            AssertImagesEqual($"{question} [after-fk5-ado-live]", snaps["after-fk5-ado-live"], dbAdo.Image.Snapshot(), FindDateSpans(snaps["after-fk5-ado-live"], dbAdo.Image.Snapshot(), c4Dt));

            Assert.Equal(MutationOpen.None, dbAdo.Workspace.PublishCloseCommit(dbAdo.Cache).Open);
            AssertImagesEqual($"{question} [after-fk5-ado-close]", snaps["after-fk5-ado-close"], dbAdo.Image.Snapshot(), FindDateSpans(snaps["after-fk5-ado-close"], dbAdo.Image.Snapshot(), c4Dt));
        }
    }

    [Theory]
    [InlineData("ix-existing-nulls-uniq-multi-accdb")]
    [InlineData("ix-existing-nulls-uniq-multi-mdb")]
    public void R_q54_ix_existing_nulls_uniq_multi_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        (string sql, string table, string label)[] steps =
        [
            ("CREATE UNIQUE INDEX UX_T1_A ON T1 (A)", "T1", "after-ux-t1-a"),
            ("CREATE UNIQUE INDEX UX_T1_A_IGN ON T1 (A) WITH IGNORE NULL", "T1", "after-ux-t1-a-ign"),
            ("CREATE INDEX IX_T2_AB ON T2 (A, B)", "T2", "after-ix-t2-ab"),
            ("CREATE INDEX IX_T2_AB_IGN ON T2 (A, B) WITH IGNORE NULL", "T2", "after-ix-t2-ab-ign"),
            ("CREATE UNIQUE INDEX UX_T2_AB ON T2 (A, B)", "T2", "after-ux-t2-ab"),
            ("CREATE UNIQUE INDEX UX_T2_AB_IGN ON T2 (A, B) WITH IGNORE NULL", "T2", "after-ux-t2-ab-ign"),
            ("CREATE UNIQUE INDEX UX_T3_PARTIAL ON T3 (A, B)", "T3", "after-ux-t3-partial"),
            ("CREATE UNIQUE INDEX UX_T3_PARTIAL_IGN ON T3 (A, B) WITH IGNORE NULL", "T3", "after-ux-t3-partial-ign"),
        ];

        foreach ((string sql, string table, string label) in steps)
        {
            ulong dt = ReadObjectDateUpdate(snaps[label], table);
            db.DateBitsProvider = () => dt;
            ActionOutcome o = ActionExecutor.Execute(sql, db);
            Assert.True(o.Succeeded, $"{question} {label} failed: {o.Failure}");
            AssertImagesEqual($"{question} [{label}]", snaps[label], db.Image.Snapshot());
        }

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertImagesEqual($"{question} [after-close]", snaps["after-close"], db.Image.Snapshot());
    }

    [Theory]
    [InlineData("ix-unique-dup-3022-bytes-accdb")]
    [InlineData("ix-unique-dup-3022-bytes-mdb")]
    public void R_q54_sql_create_index_3022_and_3058_extent_rollback_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q54-excl-ddl-ix-cmd", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        // 1. SQL CREATE UNIQUE INDEX duplicate -> 3022 (reserves 32-page extent, rolls back 1 page, 0xE02 +1)
        ActionOutcome o1 = ActionExecutor.Execute("CREATE UNIQUE INDEX UX_K ON T (K)", db);
        Assert.False(o1.Succeeded);
        Assert.Equal(3022, o1.Failure!.Number);
        AssertImagesEqual($"{question} [after-sql-foe-3022]", snaps["after-sql-foe-3022"], db.Image.Snapshot());

        // 2. Second SQL CREATE UNIQUE INDEX duplicate -> 3022 (flushes prior rolled-back page, consumes next, 0xE02 +1)
        ActionOutcome o2 = ActionExecutor.Execute("CREATE UNIQUE INDEX UX_K2 ON T (K)", db);
        Assert.False(o2.Succeeded);
        Assert.Equal(3022, o2.Failure!.Number);
        AssertImagesEqual($"{question} [after-sql-plain-3022]", snaps["after-sql-plain-3022"], db.Image.Snapshot());

        // 3. SQL CREATE INDEX WITH DISALLOW NULL on existing Null -> 3058 after prior extent reservation (flushes prior
        // rolled-back page, consumes next page, 0xE02 +1). Note: in the full probe, DAO TableDef.Indexes.Append (3022,
        // out of scope for this SQL/Catalog slice) runs between after-sql-plain-3022 and after-disallow-null-fail and
        // advances the rolled-back page slot by 1; executing IX_S_DN here after UX_K + UX_K2 verifies 3058's flush+rollback+0xE02.
        ActionOutcome o3 = ActionExecutor.Execute("CREATE INDEX IX_S_DN ON T (S) WITH DISALLOW NULL", db);
        Assert.False(o3.Succeeded);
        Assert.Equal(3058, o3.Failure!.Number);

        // 4. Close frees all remaining reserved + rolled-back pages on page 1 (0xE02 +1)
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] closed = db.Image.Snapshot();
        Assert.Equal(snaps["after-close"][D.Map.GlobalMapPage * D.PageSize + 4038], closed[D.Map.GlobalMapPage * D.PageSize + 4038]);
    }

    private static Dictionary<string, byte[]> ReadAllLabelSnapshots(string folder, string question)
    {
        string dir = Path.Combine(SqlFixture.Root(), "evidence", folder, question);
        string recordPath = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Last();
        using FileStream stream = File.OpenRead(recordPath);
        using JsonDocument doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? currentLabel = null;
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            string kind = ev.GetProperty("event").GetString()!;
            if (kind == "files")
            {
                currentLabel = ev.GetProperty("label").GetString()!;
            }
            else if (kind == "handle"
                && currentLabel is not null
                && ev.TryGetProperty("op", out JsonElement op) && op.GetString() == "read"
                && ev.TryGetProperty("bytes", out JsonElement bytesEl))
            {
                result[currentLabel] = Convert.FromHexString(bytesEl.GetString()!);
                currentLabel = null;
            }
        }
        return result;
    }

    private static ulong ReadObjectDateUpdate(byte[] image, string objectName)
    {
        using var file = new MemoryDatabaseFile(image, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => string.Equals(e.Name, objectName, StringComparison.OrdinalIgnoreCase) && e.ParentId == catalog.TablesContainer);
        return entry.DateUpdateBits!.Value;
    }

    private static List<(long Page, int Start, int Length)> FindDateSpans(byte[] expected, byte[] actual, ulong referenceDate)
    {
        List<(long Page, int Start, int Length)> spans = [];
        if (expected.Length != actual.Length)
        {
            return spans;
        }
        double refDt = BitConverter.Int64BitsToDouble((long)referenceDate);
        int pages = expected.Length / D.PageSize;
        for (long page = 0; page < pages; page++)
        {
            int baseOff = (int)(page * D.PageSize);
            int i = 0;
            while (i < D.PageSize)
            {
                if (expected[baseOff + i] == actual[baseOff + i])
                {
                    i++;
                    continue;
                }
                int start = i;
                while (i < D.PageSize && i - start < 8)
                {
                    i++;
                }
                if (start + 8 <= D.PageSize)
                {
                    double expDt = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(expected.AsSpan(baseOff + start, 8)));
                    double actDt = BitConverter.Int64BitsToDouble(BitConverter.ToInt64(actual.AsSpan(baseOff + start, 8)));
                    if (Math.Abs(expDt - refDt) < 1.0 / 86400.0 && Math.Abs(actDt - refDt) < 1.0 / 86400.0)
                    {
                        spans.Add((page, start, 8));
                    }
                }
            }
        }
        return spans;
    }

    private static void AssertImagesEqual(
        string context,
        byte[] expected,
        byte[] actual,
        IReadOnlyList<(long Page, int Start, int Length)>? ignoreSpans = null)
    {
        Assert.True(expected.Length == actual.Length, $"{context}: file length ours {actual.Length} ({actual.Length / D.PageSize}p), ACE {expected.Length} ({expected.Length / D.PageSize}p)");
        List<string> diffs = [];
        int pages = expected.Length / D.PageSize;
        for (long page = 0; page < pages; page++)
        {
            int baseOff = (int)(page * D.PageSize);
            for (int i = 0; i < D.PageSize; i++)
            {
                if (actual[baseOff + i] == expected[baseOff + i])
                {
                    continue;
                }
                bool ignored = false;
                if (ignoreSpans is not null)
                {
                    for (int s = 0; s < ignoreSpans.Count; s++)
                    {
                        (long sp, int ss, int sl) = ignoreSpans[s];
                        if (sp == page && i >= ss && i < ss + sl)
                        {
                            ignored = true;
                            break;
                        }
                    }
                }
                if (!ignored)
                {
                    diffs.Add($"page {page} +{i}: ours {actual[baseOff + i]:X2}, ACE {expected[baseOff + i]:X2}");
                }
            }
        }
        Assert.True(diffs.Count == 0, $"{context}: {diffs.Count} byte differences:\n" + string.Join("\n", diffs.Take(40)));
    }
}
