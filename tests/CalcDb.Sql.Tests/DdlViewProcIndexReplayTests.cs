using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Byte-exact kept-page replay tests for W-07/ddl-full-coverage (r-q50 Part B qkrw Stretch 2):
/// ADO CREATE/DROP VIEW, ADO CREATE/EXEC/DROP PROCEDURE, DAO 3290/3129/3295 0-byte refusals,
/// single-statement 6-constraint CREATE TABLE, and SQL CREATE INDEX WITH IGNORE NULL / DISALLOW NULL / PRIMARY
/// across ACCDB &amp; Jet 4 MDB (evidence/r-q50-jk44-ddl-vis/*).
/// </summary>
public sealed class DdlViewProcIndexReplayTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData("ddl-view-create-drop-accdb")]
    [InlineData("ddl-view-create-drop-mdb")]
    public void R_q50_ado_create_and_drop_view_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        // CREATE VIEW V1
        byte[] expV1 = snaps["after-create-v1"];
        ulong v1Date = ReadObjectDateUpdate(expV1, "V1");
        db.DateBitsProvider = () => v1Date;
        ActionOutcome o1 = ActionExecutor.Execute("CREATE VIEW V1 AS SELECT ID, A FROM T", db, source: ActionErrorSource.Ado);
        Assert.True(o1.Succeeded, $"{question} CREATE VIEW V1 failed: {o1.Failure}");
        AssertImagesEqual($"{question} [after-create-v1]", expV1, db.Image.Snapshot(), FindDateSpans(expV1, db.Image.Snapshot(), v1Date));

        // SELECT from V1
        List<List<(string Name, object? Value)>> rows = RunSelect(db, "SELECT ID, A FROM V1 ORDER BY ID");
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows[0][0].Value);
        Assert.Equal("one", rows[0][1].Value);
        Assert.Equal(2, rows[1][0].Value);
        Assert.Equal("two", rows[1][1].Value);

        // CREATE VIEW V2 WITH OWNERACCESS OPTION
        byte[] expV2 = snaps["after-create-v2"];
        ulong v2Date = ReadObjectDateUpdate(expV2, "V2");
        db.DateBitsProvider = () => v2Date;
        ActionOutcome o2 = ActionExecutor.Execute("CREATE VIEW V2 AS SELECT ID FROM T WITH OWNERACCESS OPTION", db, source: ActionErrorSource.Ado);
        Assert.True(o2.Succeeded, $"{question} CREATE VIEW V2 failed: {o2.Failure}");
        AssertImagesEqual($"{question} [after-create-v2]", expV2, db.Image.Snapshot(), FindDateSpans(expV2, db.Image.Snapshot(), v2Date));

        // Close
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] afterCreateClose = db.Image.Snapshot();
        AssertImagesEqual($"{question} [after-create-close]", snaps["after-create-close"], afterCreateClose, FindDateSpans(snaps["after-create-close"], afterCreateClose, v2Date));

        // Verify QueryCatalog & QuerySql.Render round-trip
        using (var verifyFile = new MemoryDatabaseFile(afterCreateClose, D.PageSize))
        {
            var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
            CatalogSnapshot cat = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out _)!;
            QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, cat, verifyFile.PageCount, out _)!;
            Assert.Equal(2, qcat.Queries.Count);
            SavedQuery qv1 = qcat.Queries.Single(q => q.Name == "V1");
            SavedQuery qv2 = qcat.Queries.Single(q => q.Name == "V2");
            Assert.Equal(QueryFamily.Select, qv1.Family);
            Assert.Equal("SELECT ID, A\r\nFROM T;\r\n", QuerySql.Render(qv1).Text);
            Assert.Equal(QueryFamily.Select, qv2.Family);
            Assert.Equal("SELECT ID\r\nFROM T\r\nWITH OWNERACCESS OPTION;\r\n", QuerySql.Render(qv2).Text);
        }

        // Session 2: DROP VIEW V1, DROP VIEW V2, Close
        using var db2 = new WritableSqlFixture((byte[])afterCreateClose.Clone());
        ActionOutcome d1 = ActionExecutor.Execute("DROP VIEW V1", db2, source: ActionErrorSource.Ado);
        Assert.True(d1.Succeeded, $"{question} DROP VIEW V1 failed: {d1.Failure}");
        AssertImagesEqual($"{question} [after-drop-v1]", snaps["after-drop-v1"], db2.Image.Snapshot());

        ActionOutcome d2 = ActionExecutor.Execute("DROP VIEW V2", db2, source: ActionErrorSource.Ado);
        Assert.True(d2.Succeeded, $"{question} DROP VIEW V2 failed: {d2.Failure}");
        AssertImagesEqual($"{question} [after-drop-v2]", snaps["after-drop-v2"], db2.Image.Snapshot());

        // Close (+1 on page 0)
        Assert.Equal(MutationOpen.None, db2.Workspace.PublishCounterCommit(db2.Cache).Open);
        AssertImagesEqual($"{question} [after-drop-close]", snaps["after-drop-close"], db2.Image.Snapshot());
    }

    [Theory]
    [InlineData("ddl-proc-create-exec-drop-accdb")]
    [InlineData("ddl-proc-create-exec-drop-mdb")]
    public void R_q50_ado_create_procedure_matches_ace_and_renders_sql(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        // P1: plain SELECT
        byte[] expP1 = snaps["after-create-p1"];
        ulong p1Date = ReadObjectDateUpdate(expP1, "P1");
        db.DateBitsProvider = () => p1Date;
        ActionOutcome o1 = ActionExecutor.Execute("CREATE PROCEDURE P1 AS SELECT ID, V FROM T WHERE V > 10", db, source: ActionErrorSource.Ado);
        Assert.True(o1.Succeeded, $"{question} CREATE PROCEDURE P1 failed: {o1.Failure}");
        AssertImagesEqual($"{question} [after-create-p1]", expP1, db.Image.Snapshot());

        // P2: parameterized UPDATE
        ulong p2Date = ReadObjectDateUpdate(snaps["after-create-p2"], "P2");
        db.DateBitsProvider = () => p2Date;
        ActionOutcome o2 = ActionExecutor.Execute("CREATE PROCEDURE P2 (pId LONG, pTxt TEXT(20)) AS UPDATE T SET A = pTxt WHERE ID = pId", db, source: ActionErrorSource.Ado);
        Assert.True(o2.Succeeded, $"{question} CREATE PROCEDURE P2 failed: {o2.Failure}");

        // P3: parameterized SELECT
        ulong p3Date = ReadObjectDateUpdate(snaps["after-create-p3"], "P3");
        db.DateBitsProvider = () => p3Date;
        ActionOutcome o3 = ActionExecutor.Execute("CREATE PROCEDURE P3 (pMin LONG) AS SELECT ID, V FROM T WHERE V >= pMin ORDER BY ID", db, source: ActionErrorSource.Ado);
        Assert.True(o3.Succeeded, $"{question} CREATE PROCEDURE P3 failed: {o3.Failure}");

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] img = db.Image.Snapshot();

        using (var verifyFile = new MemoryDatabaseFile(img, D.PageSize))
        {
            var verifyCache = new PageCache(verifyFile, verifyFile.Incarnation, D.PageSize, 64);
            CatalogSnapshot cat = CatalogBootstrap.Open(verifyCache, D, verifyFile.PageCount, out _)!;
            QueryCatalog qcat = QueryCatalog.Read(verifyCache, D, cat, verifyFile.PageCount, out _)!;
            Assert.Equal(3, qcat.Queries.Count);
            SavedQuery qp1 = qcat.Queries.Single(q => q.Name == "P1");
            SavedQuery qp2 = qcat.Queries.Single(q => q.Name == "P2");
            SavedQuery qp3 = qcat.Queries.Single(q => q.Name == "P3");
            Assert.Equal(QueryFamily.Select, qp1.Family);
            Assert.Equal("SELECT ID, V\r\nFROM T\r\nWHERE V > 10;\r\n", QuerySql.Render(qp1).Text);
            Assert.Equal(QueryFamily.Update, qp2.Family);
            Assert.Equal("PARAMETERS pId Long, pTxt Text ( 20 );\r\nUPDATE T SET A = pTxt\r\nWHERE ID = pId;\r\n", QuerySql.Render(qp2).Text);
            Assert.Equal(QueryFamily.Select, qp3.Family);
            Assert.Equal("PARAMETERS pMin Long;\r\nSELECT ID, V\r\nFROM T\r\nWHERE V >= pMin\r\nORDER BY ID;\r\n", QuerySql.Render(qp3).Text);
        }

        // DROP PROCEDURE P1, P2, P3
        using var db2 = new WritableSqlFixture((byte[])img.Clone());
        Assert.True(ActionExecutor.Execute("DROP PROCEDURE P1", db2, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("DROP PROCEDURE P2", db2, source: ActionErrorSource.Ado).Succeeded);
        Assert.True(ActionExecutor.Execute("DROP PROCEDURE P3", db2, source: ActionErrorSource.Ado).Succeeded);
    }

    [Theory]
    [InlineData("ddl-view-proc-dao-errors-accdb")]
    [InlineData("ddl-view-proc-dao-errors-mdb")]
    public void R_q50_dao_view_proc_exec_drop_errors_write_zero_bytes(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        byte[] start = snaps["after-setup"];
        using var db = new WritableSqlFixture((byte[])start.Clone());

        (string Sql, int ExpectedError)[] cases =
        [
            ("CREATE VIEW V_DAO AS SELECT ID FROM T", 3290),
            ("CREATE PROCEDURE P_DAO AS SELECT ID FROM T", 3290),
            ("CREATE PROCEDURE P_DAO2 (pId LONG) AS DELETE FROM T WHERE ID = pId", 3290),
            ("EXEC P1", 3129),
            ("DROP VIEW V1", 3295),
            ("DROP PROCEDURE P1", 3295),
            ("DROP VIEW NoSuchView", 3295),
            ("DROP PROCEDURE NoSuchProc", 3295),
        ];

        foreach ((string sql, int expected) in cases)
        {
            ActionOutcome outcome = ActionExecutor.Execute(sql, db, options: 128, source: ActionErrorSource.Database);
            Assert.False(outcome.Succeeded, $"{question}: `{sql}` should have failed {expected}");
            Assert.NotNull(outcome.Failure);
            Assert.False(outcome.Failure!.IsNotModelled, $"{question}: `{sql}` returned {outcome.Failure}");
            Assert.Equal(expected, outcome.Failure.Number);
            AssertImagesEqual($"{question} [{sql}]", start, db.Image.Snapshot());
        }
    }

    [Theory]
    [InlineData("ddl-multi-constraint-table-accdb")]
    [InlineData("ddl-multi-constraint-table-mdb")]
    public void R_q50_multi_constraint_create_table_matches_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-parent"].Clone());

        byte[] expLive = snaps["after-ddl-live"];
        ulong childDate = ReadObjectDateUpdate(expLive, "Child");
        db.DateBitsProvider = () => childDate;

        const string childSql =
            "CREATE TABLE Child (ID LONG NOT NULL CONSTRAINT PK_Child PRIMARY KEY, "
            + "PID LONG NOT NULL CONSTRAINT FK_Child_P REFERENCES P(ID) ON DELETE CASCADE ON UPDATE CASCADE, "
            + "Code TEXT(20) NOT NULL DEFAULT 'def' CONSTRAINT UQ_Child_Code UNIQUE, "
            + "Score LONG NOT NULL DEFAULT 10, "
            + "CONSTRAINT CK_Child_Score CHECK (Score >= 0 AND Score <= 100))";

        ActionOutcome outcome = ActionExecutor.Execute(childSql, db, source: ActionErrorSource.Ado);
        Assert.True(outcome.Succeeded, $"{question} CREATE TABLE Child failed: {outcome.Failure}");
        AssertImagesEqual($"{question} [after-ddl-live]", expLive, db.Image.Snapshot(), FindDateSpans(expLive, db.Image.Snapshot(), childDate));

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] expClose = snaps["after-ddl-close"];
        AssertImagesEqual($"{question} [after-ddl-close]", expClose, db.Image.Snapshot(), FindDateSpans(expClose, db.Image.Snapshot(), childDate));
    }

    [Fact]
    public void R_q50_create_index_opts_empty_mdb_matches_ace_byte_exact()
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", "ddl-index-opts-empty-mdb");
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        (string Sql, string Table, string Label)[] steps =
        [
            ("CREATE INDEX IX1 ON T1 (N) WITH IGNORE NULL", "T1", "after-ign"),
            ("CREATE UNIQUE INDEX IX2 ON T2 (N) WITH IGNORE NULL", "T2", "after-uq-ign"),
            ("CREATE INDEX IX3 ON T3 (N) WITH DISALLOW NULL", "T3", "after-dis"),
            ("CREATE UNIQUE INDEX IX4 ON T4 (N) WITH DISALLOW NULL", "T4", "after-uq-dis"),
            ("CREATE INDEX IX5 ON T5 (N) WITH PRIMARY", "T5", "after-pri"),
        ];

        foreach ((string sql, string table, string label) in steps)
        {
            byte[] exp = snaps[label];
            db.DateBitsProvider = () => ReadObjectDateUpdate(exp, table);
            ActionOutcome o = ActionExecutor.Execute(sql, db, options: 128, source: ActionErrorSource.Database);
            Assert.True(o.Succeeded, $"ddl-index-opts-empty-mdb [{label}] `{sql}` failed: {o.Failure}");
            AssertImagesEqual($"ddl-index-opts-empty-mdb [{label}]", exp, db.Image.Snapshot());
        }

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertImagesEqual("ddl-index-opts-empty-mdb [after-close]", snaps["after-close"], db.Image.Snapshot());
    }

    [Theory]
    [InlineData("ddl-index-ignore-null-rows-accdb")]
    [InlineData("ddl-index-ignore-null-rows-mdb")]
    public void R_q50_create_index_ignore_null_and_3058_rows_match_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        byte[] expIgn = snaps["after-ix-ign"];
        db.DateBitsProvider = () => ReadObjectDateUpdate(expIgn, "T");
        ActionOutcome o1 = ActionExecutor.Execute("CREATE INDEX IX_K_IGN ON T (K) WITH IGNORE NULL", db, options: 128, source: ActionErrorSource.Database);
        Assert.True(o1.Succeeded, $"{question} IX_K_IGN failed: {o1.Failure}");
        AssertImagesEqual($"{question} [after-ix-ign]", expIgn, db.Image.Snapshot());

        byte[] expUqIgn = snaps["after-ux-ign"];
        db.DateBitsProvider = () => ReadObjectDateUpdate(expUqIgn, "T");
        ActionOutcome o2 = ActionExecutor.Execute("CREATE UNIQUE INDEX UX_U_IGN ON T (U) WITH IGNORE NULL", db, options: 128, source: ActionErrorSource.Database);
        Assert.True(o2.Succeeded, $"{question} UX_U_IGN failed: {o2.Failure}");
        AssertImagesEqual($"{question} [after-ux-ign]", expUqIgn, db.Image.Snapshot());

        ActionOutcome o3 = ActionExecutor.Execute("CREATE INDEX IX_K_DIS ON T (K) WITH DISALLOW NULL", db, options: 128, source: ActionErrorSource.Database);
        Assert.False(o3.Succeeded);
        Assert.Equal(3058, o3.Failure!.Number);
        AssertImagesEqual($"{question} [after-dis-fail]", snaps["after-dis-fail"], db.Image.Snapshot());

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertImagesEqual($"{question} [after-close]", snaps["after-close"], db.Image.Snapshot());
    }

    [Theory]
    [InlineData("ddl-index-disallow-primary-rows-accdb")]
    [InlineData("ddl-index-disallow-primary-rows-mdb")]
    public void R_q50_create_index_disallow_and_primary_rows_match_ace_byte_exact(string question)
    {
        Dictionary<string, byte[]> snaps = ReadAllLabelSnapshots("r-q50-jk44-ddl-vis", question);
        using var db = new WritableSqlFixture((byte[])snaps["after-setup"].Clone());

        byte[] expDis = snaps["after-dis-ok"];
        db.DateBitsProvider = () => ReadObjectDateUpdate(expDis, "T1");
        ActionOutcome o1 = ActionExecutor.Execute("CREATE INDEX IX_B_DIS ON T1 (B) WITH DISALLOW NULL", db, options: 128, source: ActionErrorSource.Database);
        Assert.True(o1.Succeeded, $"{question} IX_B_DIS failed: {o1.Failure}");
        AssertImagesEqual($"{question} [after-dis-ok]", expDis, db.Image.Snapshot());

        byte[] expPri = snaps["after-pri-ok"];
        db.DateBitsProvider = () => ReadObjectDateUpdate(expPri, "T1");
        ActionOutcome o2 = ActionExecutor.Execute("CREATE INDEX IX_A_PRI ON T1 (A) WITH PRIMARY", db, options: 128, source: ActionErrorSource.Database);
        Assert.True(o2.Succeeded, $"{question} IX_A_PRI failed: {o2.Failure}");
        AssertImagesEqual($"{question} [after-pri-ok]", expPri, db.Image.Snapshot());

        ActionOutcome o3 = ActionExecutor.Execute("CREATE INDEX IX_T2_PRI ON T2 (B) WITH PRIMARY", db, options: 128, source: ActionErrorSource.Database);
        Assert.False(o3.Succeeded);
        Assert.Equal(3058, o3.Failure!.Number);
        AssertImagesEqual($"{question} [after-pri-fail]", snaps["after-pri-fail"], db.Image.Snapshot());

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertImagesEqual($"{question} [after-close]", snaps["after-close"], db.Image.Snapshot());
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static string RecordPath(string card, string question)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "evidence", card, question));
        return Directory.EnumerateFiles(dir, "*.a1.json").Single();
    }

    private static Dictionary<string, byte[]> ReadAllLabelSnapshots(string card, string question)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(RecordPath(card, question)));
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
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

    private static List<List<(string Name, object? Value)>> RunSelect(ISqlDatabase db, string sql)
    {
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? fail);
        Assert.Null(fail);
        Assert.NotNull(plan);
        using SelectCursor? cursor = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [], out SqlFailure? openFail);
        Assert.Null(openFail);
        Assert.NotNull(cursor);

        List<List<(string Name, object? Value)>> rows = [];
        while (!cursor!.Eof)
        {
            var row = new List<(string Name, object? Value)>(plan!.Columns.Count);
            for (int i = 0; i < plan.Columns.Count; i++)
            {
                Assert.True(cursor.Read(i, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
                object? obj = v.Type switch
                {
                    VarType.Null or VarType.Empty => null,
                    VarType.Bool => (short)v.Int64 != 0,
                    VarType.I2 => (int)(short)v.Int64,
                    VarType.I4 => (int)v.Int64,
                    VarType.I8 => v.Int64,
                    VarType.R8 => v.Double,
                    VarType.Date => v.Double,
                    VarType.Bstr => v.Pointer == 0 ? "" : new string(Bstrs.Chars(v.Pointer)),
                    _ => v.Type.ToString(),
                };
                row.Add((plan.Columns[i].Name, obj));
            }
            rows.Add(row);
            cursor.MoveNext();
        }
        return rows;
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
