using System.Text.Json;

using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Storage;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// SQL DDL parser, binder, and executor tests (W-07/native-metadata-write stretch 2):
/// byte-exact <c>evidence/r-q11-ddl-closure</c> round-trips through <see cref="ActionExecutor.Execute"/>,
/// end-to-end DDL + DML + SELECT readback, ACE syntax/bind error numbers, <c>evidence/r-q15-ddl-open</c>
/// declaration-order / NOT NULL / 3010 rules, and named <c>NotModelled.Ddl*</c> refusals.
/// </summary>
public sealed class DdlTests
{
    private const string EmptyAccdbSha = "ea467b4c6d6472bc6819b0ddbe535612db76cc9d6bf2bf581d08f149c3765df8";
    private const string EmptyMdbSha = "99da59149c57559b5658a9a08625236a936808d2be0ead100da977bd3716bdb4";
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;

    [Theory]
    [InlineData("create-one-long", new[] { "T" }, new[] { "CREATE TABLE T (n LONG)" })]
    [InlineData("create-name-first", new[] { "A" }, new[] { "CREATE TABLE A (n LONG)" })]
    [InlineData("create-two-columns", new[] { "T" }, new[] { "CREATE TABLE T (n LONG, t TEXT(50))" })]
    [InlineData("create-primary-key", new[] { "T" }, new[] { "CREATE TABLE T (n LONG CONSTRAINT PK_T PRIMARY KEY)" })]
    [InlineData("create-two-tables", new[] { "T", "U" }, new[] { "CREATE TABLE T (n LONG)", "CREATE TABLE U (n LONG)" })]
    public void R_q11_ddl_closure_create_table_executes_byte_exact_through_sql_ddl(string question, string[] createdTables, string[] statements)
    {
        Kept kept = Kept.Of(question);
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        byte[] aceImage = kept.AceImage(fixture);
        var timestamps = new Queue<ulong>();
        foreach (string table in createdTables)
        {
            (ulong created, ulong updated) = AceDates(aceImage, table);
            timestamps.Enqueue(created);
            timestamps.Enqueue(updated);
        }

        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        foreach (string sql in statements)
        {
            ActionOutcome outcome = ActionExecutor.Execute(sql, db);
            Assert.Null(outcome.Failure);
            Assert.True(outcome.Succeeded);
            Assert.Equal(0, outcome.RecordsAffected);
        }

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void R_q11_ddl_closure_create_then_index_executes_byte_exact_through_sql_ddl()
    {
        Kept kept = Kept.Of("create-then-index");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        Assert.NotEqual(created, updated);

        var timestamps = new Queue<ulong>([created, created, updated]);
        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        Assert.True(ActionExecutor.Execute("CREATE TABLE T (n LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_T ON T (n)", db).Succeeded);
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void R_q15_columns_z_a_executes_byte_exact_through_sql_ddl()
    {
        // r-q15-ddl-open/columns-z-a: the column blocks in definition order (z, then a) on page 42.
        Kept kept = Kept.Of("columns-z-a", "r-q15-ddl-open");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");

        var timestamps = new Queue<ulong>([created, updated]);
        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome outcome = ActionExecutor.Execute("CREATE TABLE T (z LONG, a LONG)", db);
        Assert.Null(outcome.Failure);
        Assert.True(outcome.Succeeded);
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void R_q11_ddl_closure_create_then_drop_executes_byte_exact_through_sql_ddl()
    {
        Kept kept = Kept.Of("create-then-drop");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        byte[] ace = kept.AceImage(fixture);

        // Locate the dropped MSysObjects row slot in page 17's free gap to recover ACE's DateCreate/DateUpdate clock readings.
        ulong created, updated;
        using (var probe = new WritableSqlFixture(fixture))
        {
            probe.DateBitsProvider = () => 1;
            Assert.True(ActionExecutor.Execute("CREATE TABLE T (n LONG)", probe).Succeeded);
            CatalogSnapshot catalog = CatalogBootstrap.Open(probe.Cache, D, probe.PageCount, out _)!;
            CatalogEntry t = catalog.Entries.Single(e => e.Name == "T");
            using PagePin page = probe.Cache.Pin(t.Root.Page).Pin!;
            SlotView slot = DataPages.CheckedSlot(page.Bytes, t.Root.Row, D, out _)!.Value;
            int DateOffset(string col) => (int)(t.Root.Page * (ulong)D.PageSize) + slot.Start + D.Row.FixedBase
                + catalog.ObjectTable.Columns.Single(c => c.Name == col).FixedOffset;
            created = BitConverter.ToUInt64(ace, DateOffset("DateCreate"));
            updated = BitConverter.ToUInt64(ace, DateOffset("DateUpdate"));
        }

        var timestamps = new Queue<ulong>([created, updated]);
        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (n LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("DROP TABLE T", db).Succeeded);
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Theory]
    [InlineData("col-memo", "CREATE TABLE T (n MEMO)")]
    [InlineData("col-longtext", "CREATE TABLE T (n LONGTEXT)")]
    [InlineData("col-two-memos", "CREATE TABLE T (a MEMO, b MEMO)")]
    [InlineData("col-counter", "CREATE TABLE T (n COUNTER)")]
    [InlineData("col-autoincrement", "CREATE TABLE T (n AUTOINCREMENT)")]
    [InlineData("col-counter-seed-inc", "CREATE TABLE T (n COUNTER(10, 5))")]
    [InlineData("col-counter-100-7", "CREATE TABLE T (n COUNTER(100, 7))")]
    [InlineData("col-counter-neg-inc", "CREATE TABLE T (n COUNTER(10, -2))")]
    [InlineData("col-double", "CREATE TABLE T (n DOUBLE)")]
    [InlineData("col-byte", "CREATE TABLE T (n BYTE)")]
    [InlineData("col-short", "CREATE TABLE T (n SHORT)")]
    [InlineData("col-integer", "CREATE TABLE T (n INTEGER)")]
    [InlineData("col-single", "CREATE TABLE T (n SINGLE)")]
    [InlineData("col-currency", "CREATE TABLE T (n CURRENCY)")]
    [InlineData("col-datetime", "CREATE TABLE T (n DATETIME)")]
    [InlineData("col-yesno", "CREATE TABLE T (n YESNO)")]
    [InlineData("col-bit", "CREATE TABLE T (n BIT)")]
    [InlineData("col-yesno-long", "CREATE TABLE T (a YESNO, b LONG)")]
    [InlineData("col-guid", "CREATE TABLE T (n GUID)")]
    [InlineData("col-fixed-guid-memo", "CREATE TABLE T (a LONG, b GUID, c MEMO)")]
    [InlineData("col-longbinary", "CREATE TABLE T (n LONGBINARY)")]
    [InlineData("col-oleobject", "CREATE TABLE T (n OLEOBJECT)")]
    [InlineData("col-binary-16", "CREATE TABLE T (n BINARY(16))")]
    [InlineData("col-binary-1", "CREATE TABLE T (n BINARY(1))")]
    [InlineData("col-binary-bare", "CREATE TABLE T (n BINARY)")]
    [InlineData("col-varbinary-16", "CREATE TABLE T (n VARBINARY(16))")]
    [InlineData("col-binary-long", "CREATE TABLE T (a BINARY(16), b LONG)")]
    public void R_q21_ddl_types_create_table_executes_byte_exact_through_sql_ddl(string question, string sql)
    {
        Kept kept = Kept.Of(question, "r-q21-ddl-types");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        var timestamps = new Queue<ulong>([created, updated]);

        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome outcome = ActionExecutor.Execute(sql, db);
        Assert.Null(outcome.Failure);
        Assert.True(outcome.Succeeded);
        Assert.True(outcome.PreservesRecordsAffected);
        Assert.Equal(0, outcome.RecordsAffected);

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Theory]
    [InlineData("sql-100-5-ins1", "CREATE TABLE T (n COUNTER(100, 5), v LONG)", new[] { "INSERT INTO T (v) VALUES (1)" }, new long[] { 100 })]
    [InlineData("sql-100-5-ins2", "CREATE TABLE T (n COUNTER(100, 5), v LONG)", new[] { "INSERT INTO T (v) VALUES (1)", "INSERT INTO T (v) VALUES (2)" }, new long[] { 100, 105 })]
    [InlineData("sql-100-1-ins1", "CREATE TABLE T (n COUNTER(100, 1), v LONG)", new[] { "INSERT INTO T (v) VALUES (1)" }, new long[] { 100 })]
    [InlineData("sql-10-neg2-ins1", "CREATE TABLE T (n COUNTER(10, -2), v LONG)", new[] { "INSERT INTO T (v) VALUES (1)" }, new long[] { 10 })]
    [InlineData("sql-10-neg2-ins2", "CREATE TABLE T (n COUNTER(10, -2), v LONG)", new[] { "INSERT INTO T (v) VALUES (1)", "INSERT INTO T (v) VALUES (2)" }, new long[] { 10, 8 })]
    public void R_q23_counter_step_sql_insert_executes_byte_exact_against_kept_pages(
        string question, string createSql, string[] insertSqls, long[] expectedIssued)
    {
        Kept kept = Kept.Of(question, "r-q23-counter-step");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        var timestamps = new Queue<ulong>([created, updated]);

        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome create = ActionExecutor.Execute(createSql, db);
        Assert.Null(create.Failure);
        Assert.True(create.Succeeded);

        foreach (string insSql in insertSqls)
        {
            ActionOutcome ins = ActionExecutor.Execute(insSql, db);
            Assert.Null(ins.Failure);
            Assert.True(ins.Succeeded);
            Assert.Equal(1, ins.RecordsAffected);
        }

        using BoundSelect plan = BoundSelect.Prepare("SELECT n, v FROM T ORDER BY v", db, out SqlFailure? selFail)!;
        Assert.Null(selFail);
        using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, [], out selFail)!;
        Assert.Null(selFail);
        var issued = new List<long>();
        while (!cur.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out _));
            issued.Add((int)v.Int64);
            cur.MoveNext();
        }
        Assert.Equal(expectedIssued, issued);

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void R_q23_counter_step_dao_addnew_executes_byte_exact_against_kept_pages()
    {
        Kept kept = Kept.Of("dao-100-5-addnew1", "r-q23-counter-step");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        var timestamps = new Queue<ulong>([created, updated]);

        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome create = ActionExecutor.Execute("CREATE TABLE T (n COUNTER(100, 5), v LONG)", db);
        Assert.Null(create.Failure);
        Assert.True(create.Succeeded);

        TableDefinition table = db.Table("T")!;
        TableEditContext ctx = db.EditContext("T", out string? ctxFail)!;
        Assert.Null(ctxFail);
        using TableCursor cursor = db.OpenCursor(table, out string? curFail)!;
        Assert.Null(curFail);
        using var frame = new EditFrame(ctx, cursor, db.Workspace);

        Assert.True(frame.AddNew().Succeeded);
        // DAO dynaset shows the issued value (100) immediately at AddNew before Update.
        FieldResult atAddNew = frame.Read(table.Columns[0]);
        Assert.True(atAddNew.Succeeded);
        Assert.Equal(100L, atAddNew.Value.Integer);

        Assert.True(frame.Assign(table.Columns[1], new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 1 }).Succeeded);
        UpdateOutcome upd = frame.Update();
        Assert.True(upd.Succeeded, upd.ToString());

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void Counter_with_custom_seed_and_increment_steps_and_refuses_open_cases_by_name()
    {
        static List<long> ReadColumnA(WritableSqlFixture db, string table)
        {
            using BoundSelect plan = BoundSelect.Prepare($"SELECT A FROM {table} ORDER BY B", db, out SqlFailure? sf)!;
            Assert.Null(sf);
            using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, [], out sf)!;
            Assert.Null(sf);
            var values = new List<long>();
            while (!cur.Eof)
            {
                Assert.True(cur.Read(0, out NativeVariant v, out _));
                values.Add((int)v.Int64);
                cur.MoveNext();
            }
            return values;
        }

        using var db = new WritableSqlFixture(EmptyAccdbSha);

        // r-q23-counter-step/sql-100-5-ins1, sql-100-5-ins2; r-def-expr-sql/ddl-counter-seed-increment Cs1:
        // COUNTER(100, 5) issues 100, 105; explicit value under non-1 increment refuses by name without consuming counter.
        Assert.True(ActionExecutor.Execute("CREATE TABLE Cs1 (A COUNTER(100, 5), B LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs1 (B) VALUES (1)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs1 (B) VALUES (2)", db).Succeeded);
        ActionOutcome explicitInc5 = ActionExecutor.Execute("INSERT INTO Cs1 (A, B) VALUES (50, 3)", db);
        Assert.False(explicitInc5.Succeeded);
        Assert.Equal("NotModelled.Edit.AutoNumberIncrement", explicitInc5.Failure?.NotModelled);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs1 (B) VALUES (4)", db).Succeeded);
        Assert.Equal([100L, 105L, 110L], ReadColumnA(db, "Cs1"));

        // r-q23-counter-step/sql-10-neg2-ins1, sql-10-neg2-ins2: negative increment steps down through 0 into negative.
        Assert.True(ActionExecutor.Execute("CREATE TABLE CsNeg (A COUNTER(10, -2), B LONG)", db).Succeeded);
        for (int b = 1; b <= 7; b++)
        {
            Assert.True(ActionExecutor.Execute($"INSERT INTO CsNeg (B) VALUES ({b})", db).Succeeded);
        }
        ActionOutcome explicitNeg = ActionExecutor.Execute("INSERT INTO CsNeg (A, B) VALUES (50, 8)", db);
        Assert.False(explicitNeg.Succeeded);
        Assert.Equal("NotModelled.Edit.AutoNumberIncrement", explicitNeg.Failure?.NotModelled);
        Assert.Equal([10L, 8L, 6L, 4L, 2L, 0L, -2L], ReadColumnA(db, "CsNeg"));

        // Negative initial TDEF 0x14 with unit increment (Cs3 COUNTER(0, 1) -> -1, 1) and positive increment (Cs6 COUNTER(-5, 2) -> -7, 2).
        Assert.True(ActionExecutor.Execute("CREATE TABLE Cs3 (A COUNTER(0, 1), B LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs3 (B) VALUES (1)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs3 (B) VALUES (2)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs3 (A, B) VALUES (50, 3)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs3 (B) VALUES (4)", db).Succeeded);
        Assert.Equal([0L, 1L, 50L, 51L], ReadColumnA(db, "Cs3"));

        Assert.True(ActionExecutor.Execute("CREATE TABLE Cs6 (A COUNTER(-5, 2), B LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs6 (B) VALUES (1)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs6 (B) VALUES (2)", db).Succeeded);
        Assert.Equal([-5L, -3L], ReadColumnA(db, "Cs6"));

        // Overflow past int.MaxValue and underflow below int.MinValue refuse by name (qualified.md Open).
        Assert.True(ActionExecutor.Execute("CREATE TABLE Cs7 (A COUNTER(2147483647, 1), B LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Cs7 (B) VALUES (1)", db).Succeeded);
        ActionOutcome ovfInsert = ActionExecutor.Execute("INSERT INTO Cs7 (B) VALUES (2)", db);
        Assert.False(ovfInsert.Succeeded);
        Assert.Equal("NotModelled.Edit.AutoNumberOverflow", ovfInsert.Failure?.NotModelled);
        Assert.Equal([2147483647L], ReadColumnA(db, "Cs7"));

        Assert.True(ActionExecutor.Execute("CREATE TABLE CsMin (A COUNTER(-2147483647, -2), B LONG)", db).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO CsMin (B) VALUES (1)", db).Succeeded);
        ActionOutcome unfInsert = ActionExecutor.Execute("INSERT INTO CsMin (B) VALUES (2)", db);
        Assert.False(unfInsert.Succeeded);
        Assert.Equal("NotModelled.Edit.AutoNumberOverflow", unfInsert.Failure?.NotModelled);
        Assert.Equal([-2147483647L], ReadColumnA(db, "CsMin"));

        // Seed - increment int32 overflow at CREATE TABLE refuses by name.
        AssertNotModelled(db, "CREATE TABLE CsOvf (A COUNTER(-2147483648, 1))", "NotModelled.DdlCounterOverflow");
    }

    [Fact]
    public void Counter_with_unit_increment_accepts_custom_seed_and_records_affected_persists_across_ddl()
    {
        // r-def-expr-sql/ddl-counter-seed-increment (Cs2 AUTOINCREMENT(10, 1); #88-89, #168-169, #328-360)
        // and r-sql-2-3/act-ddl-columns-indexes (#16-17, #31-32, #41-72, #81-82, #91-92):
        // successful DDL leaves RecordsAffected at the last successful DML count; failures read 0 without clearing it.
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        RecordsAffectedState affected = default;

        ActionOutcome create = ActionExecutor.Execute("CREATE TABLE Cs2 (A AUTOINCREMENT(10, 1), B LONG)", db);
        Assert.True(create.Succeeded);
        affected.Apply(create);
        Assert.Equal(0, affected.Current);

        ActionOutcome ins1 = ActionExecutor.Execute("INSERT INTO Cs2 (B) VALUES (1)", db);
        Assert.True(ins1.Succeeded);
        affected.Apply(ins1);
        Assert.Equal(1, affected.Current);

        ActionOutcome ins2 = ActionExecutor.Execute("INSERT INTO Cs2 (B) VALUES (2)", db);
        Assert.True(ins2.Succeeded);
        affected.Apply(ins2);
        Assert.Equal(1, affected.Current);

        ActionOutcome upd = ActionExecutor.Execute("UPDATE Cs2 SET B = B + 10", db);
        Assert.True(upd.Succeeded);
        affected.Apply(upd);
        Assert.Equal(2, affected.Current);

        // Successful DDL after DML reads the last DML count (2).
        ActionOutcome createOther = ActionExecutor.Execute("CREATE TABLE Other (K LONG)", db);
        Assert.True(createOther.Succeeded);
        affected.Apply(createOther);
        Assert.Equal(2, affected.Current);

        // Failed Execute reads 0 (SQL-ACT-FAIL) without clearing the remembered last DML count (2).
        ActionOutcome failed = ActionExecutor.Execute("CREATE TABLE Cs4 (A COUNTER(1, 0), B LONG)", db);
        Assert.False(failed.Succeeded);
        Assert.Equal(3001, failed.Failure?.Number);
        affected.Apply(failed);
        Assert.Equal(0, affected.Current);

        // Subsequent successful DDL restores the remembered last DML count (2).
        ActionOutcome createIdx = ActionExecutor.Execute("CREATE INDEX IX_Other ON Other (K)", db);
        Assert.True(createIdx.Succeeded);
        affected.Apply(createIdx);
        Assert.Equal(2, affected.Current);

        // Verify COUNTER(10, 1) generated 10, 11.
        using BoundSelect plan = BoundSelect.Prepare("SELECT A, B FROM Cs2 ORDER BY A", db, out SqlFailure? selFail)!;
        Assert.Null(selFail);
        using SelectCursor cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, [], out selFail)!;
        Assert.Null(selFail);
        var ids = new List<long>();
        while (!cur.Eof)
        {
            Assert.True(cur.Read(0, out NativeVariant v, out _));
            ids.Add(v.Int64);
            cur.MoveNext();
        }
        Assert.Equal([10L, 11L], ids);
    }

    [Fact]
    public void R_q21_ddl_types_col_decimal_dao_fails_3292_with_zero_bytes_changed()
    {
        Kept kept = Kept.Of("col-decimal-dao", "r-q21-ddl-types");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);

        using var db = new WritableSqlFixture(fixture);
        AssertAceError(db, "CREATE TABLE T (n DECIMAL(18, 4))", 3292);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Theory]
    [InlineData("ix-unique", "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n)")]
    [InlineData("ix-with-primary", "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH PRIMARY")]
    [InlineData("ix-unique-with-primary", "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH PRIMARY")]
    [InlineData("ix-disallow-null", "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH DISALLOW NULL")]
    [InlineData("ix-ignore-null", "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n) WITH IGNORE NULL")]
    [InlineData("ix-unique-disallow-null", "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH DISALLOW NULL")]
    [InlineData("ix-unique-ignore-null", "CREATE TABLE T (n LONG)", "CREATE UNIQUE INDEX IX_T ON T (n) WITH IGNORE NULL")]
    [InlineData("ix-desc", "CREATE TABLE T (n LONG)", "CREATE INDEX IX_T ON T (n DESC)")]
    [InlineData("ix-text", "CREATE TABLE T (t TEXT(50))", "CREATE INDEX IX_T ON T (t)")]
    [InlineData("ix-two-col", "CREATE TABLE T (a LONG, b LONG)", "CREATE INDEX IX_T ON T (a, b)")]
    [InlineData("ix-two-col-mixed-desc", "CREATE TABLE T (a LONG, b TEXT(50))", "CREATE INDEX IX_T ON T (a, b DESC)")]
    [InlineData("ix-on-table-with-memo", "CREATE TABLE T (a MEMO, b LONG)", "CREATE INDEX IX_T ON T (b)")]
    public void R_q21_ddl_types_create_index_executes_byte_exact_through_sql_ddl(string question, string createTableSql, string createIndexSql)
    {
        Kept kept = Kept.Of(question, "r-q21-ddl-types");
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);
        (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
        var timestamps = new Queue<ulong>([created, created, updated]);

        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome t = ActionExecutor.Execute(createTableSql, db);
        Assert.Null(t.Failure);
        Assert.True(t.Succeeded);

        ActionOutcome ix = ActionExecutor.Execute(createIndexSql, db);
        Assert.Null(ix.Failure);
        Assert.True(ix.Succeeded);
        Assert.Equal(0, ix.RecordsAffected);

        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        AssertAsAce(kept, fixture, db.Image.Snapshot());
    }

    [Fact]
    public void Create_table_then_insert_and_select_readback_works_end_to_end()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);

        ActionOutcome created = ActionExecutor.Execute(
            "CREATE TABLE Items (id LONG CONSTRAINT PK_Items PRIMARY KEY, title TEXT(20))", db);
        Assert.Null(created.Failure);
        Assert.Equal(0, created.RecordsAffected);

        ActionOutcome ins1 = ActionExecutor.Execute("INSERT INTO Items (id, title) VALUES (20, 'second')", db);
        Assert.Null(ins1.Failure);
        Assert.Equal(1, ins1.RecordsAffected);

        ActionOutcome ins2 = ActionExecutor.Execute("INSERT INTO Items (id, title) VALUES (10, 'first')", db);
        Assert.Null(ins2.Failure);
        Assert.Equal(1, ins2.RecordsAffected);

        // Dynaset on a table with PK reads in PK order (10, 20); snapshot reads in storage order (20, 10).
        using BoundSelect? plan = BoundSelect.Prepare("SELECT id, title FROM Items", db, out SqlFailure? bf);
        Assert.Null(bf);

        using (SelectCursor? dyn = SelectCursor.Open(plan!, db, ResultKind.Dynaset, [], out SqlFailure? of))
        {
            Assert.Null(of);
            var ids = new List<long>();
            while (!dyn!.Eof)
            {
                Assert.True(dyn.Read(0, out NativeVariant v, out _));
                ids.Add(v.Int64);
                dyn.MoveNext();
            }
            Assert.Equal([10L, 20L], ids);
        }

        using (SelectCursor? snap = SelectCursor.Open(plan!, db, ResultKind.Snapshot, [], out SqlFailure? of))
        {
            Assert.Null(of);
            var ids = new List<long>();
            while (!snap!.Eof)
            {
                Assert.True(snap.Read(0, out NativeVariant v, out _));
                ids.Add(v.Int64);
                snap.MoveNext();
            }
            Assert.Equal([20L, 10L], ids);
        }

        // Empty table created and dropped removes the table from catalog.
        Assert.True(ActionExecutor.Execute("CREATE TABLE Scratch (a LONG)", db).Succeeded);
        Assert.NotNull(db.Table("Scratch"));
        Assert.True(ActionExecutor.Execute("DROP TABLE Scratch", db).Succeeded);
        Assert.Null(db.Table("Scratch"));
    }

    [Theory]
    [InlineData("GRANT SELECT ON T TO Admin", 3129)]
    [InlineData("REVOKE SELECT ON T FROM Admin", 3129)]
    [InlineData("CREATE TABLE T (x LONG) EXTRA", 3290)]
    [InlineData("CREATE TABLE TABLE (x LONG)", 3290)]
    [InlineData("CREATE TABLE DATE (x LONG)", 3290)]
    [InlineData("CREATE TABLE [] (x LONG)", 3290)]
    [InlineData("CREATE TABLE [T`T] (x LONG)", 3290)]
    [InlineData("CREATE TABLE T (TEXT LONG)", 3292)]
    [InlineData("CREATE TABLE T (LONG LONG)", 3292)]
    [InlineData("CREATE TABLE T ()", 3292)]
    [InlineData("CREATE TABLE T (x NOTATYPE)", 3292)]
    [InlineData("CREATE TABLE T (x LONG DEFAULT 1)", 3290)]
    [InlineData("CREATE TABLE T (x LONG CHECK (x > 0))", 3290)]
    [InlineData("CREATE TABLE T (x LONG CONSTRAINT C CHECK (x > 0))", 3289)]
    [InlineData("CREATE INDEX ON T (x)", 3291)]
    [InlineData("DROP VIEW V", 3295)]
    [InlineData("ALTER TABLE T RENAME COLUMN x TO y", 3293)]
    public void Ddl_parser_reports_recorded_ace_syntax_error_numbers(string sql, int expectedError)
    {
        DdlStatement? stmt = DdlParser.Parse(sql, out SqlFailure? failure);
        Assert.Null(stmt);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(expectedError, failure.Number);
    }

    [Theory]
    [InlineData("CREATE TABLE T (x LONG); EXTRA", "NotModelled.DdlTrailingAfterSemicolon")]
    [InlineData("CREATE TABLE T (x LONG CONSTRAINT)", "NotModelled.DdlConstraintSyntax")]
    [InlineData("CREATE INDEX IX_T T (x)", "NotModelled.DdlCreateIndexSyntax")]
    [InlineData("DROP TABLE", "NotModelled.DdlDropSyntax")]
    [InlineData("CREATE TABLE T (x LONG NOT 1)", "NotModelled.DdlNotWithoutNull")]
    [InlineData("ALTER TABLE T ALTER COLUMN x LONG NOT 1", "NotModelled.DdlNotWithoutNull")]
    [InlineData("ALTER TABLE T ADD COLUMN x LONG(10)", "NotModelled.DdlAlterTypeSyntax")]
    [InlineData("ALTER TABLE T ADD COLUMN x NOTATYPE", "NotModelled.DdlAlterTypeSyntax")]
    [InlineData("ALTER TABLE T ALTER COLUMN x NOTATYPE", "NotModelled.DdlAlterTypeSyntax")]
    [InlineData("ALTER TABLE T ALTER COLUMN x LONG(10)", "NotModelled.DdlAlterTypeSyntax")]
    [InlineData("CREATE TABLE T (FOREIGN LONG)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE TABLE CHAR (x LONG)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE TABLE T (VARCHAR LONG)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE TABLE T (INT LONG)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE INDEX DATE ON T (x)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE INDEX IX_T ON TABLE (x)", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE INDEX IX_T ON T (TEXT)", "NotModelled.DdlReservedWord")]
    [InlineData("DROP TABLE TABLE", "NotModelled.DdlReservedWord")]
    [InlineData("DROP INDEX DATE ON T", "NotModelled.DdlReservedWord")]
    [InlineData("ALTER TABLE TABLE ADD COLUMN x LONG", "NotModelled.DdlReservedWord")]
    [InlineData("ALTER TABLE T DROP COLUMN TEXT", "NotModelled.DdlReservedWord")]
    [InlineData("ALTER TABLE T ALTER COLUMN TEXT LONG", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE TABLE T (a LONG, PRIMARY KEY (TEXT))", "NotModelled.DdlReservedWord")]
    [InlineData("CREATE TABLE T (a LONG REFERENCES TABLE (a))", "NotModelled.DdlReservedWord")]
    public void Ddl_parser_refuses_unrecorded_syntax_and_reserved_word_shapes_by_name(string sql, string expectedNotModelled)
    {
        DdlStatement? stmt = DdlParser.Parse(sql, out SqlFailure? failure);
        Assert.Null(stmt);
        Assert.NotNull(failure);
        Assert.True(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(expectedNotModelled, failure.NotModelled);
    }

    [Fact]
    public void Ddl_parser_accepts_bracketed_reserved_names_and_unbracketed_select_where()
    {
        // r-def-expr-sql/ddl-reserved-table-names & ddl-reserved-column-names: bracketed reserved words succeed,
        // and non-DDL SQL keywords like SELECT/WHERE are accepted unbracketed by ACE.
        DdlStatement? s1 = DdlParser.Parse("CREATE TABLE [TABLE] ([TEXT] LONG, [a b] TEXT(10), [CHAR] LONG, [FOREIGN] LONG)", out SqlFailure? f1);
        Assert.Null(f1);
        var ct1 = Assert.IsType<CreateTableStatement>(s1);
        Assert.Equal("TABLE", ct1.TableName);
        Assert.Equal("TEXT", ct1.Columns[0].Name);
        Assert.Equal("a b", ct1.Columns[1].Name);
        Assert.Equal("CHAR", ct1.Columns[2].Name);
        Assert.Equal("FOREIGN", ct1.Columns[3].Name);

        DdlStatement? s2 = DdlParser.Parse("CREATE TABLE SELECT (WHERE LONG)", out SqlFailure? f2);
        Assert.Null(f2);
        var ct2 = Assert.IsType<CreateTableStatement>(s2);
        Assert.Equal("SELECT", ct2.TableName);
        Assert.Equal("WHERE", ct2.Columns[0].Name);
    }

    [Fact]
    public void Ddl_binder_enforces_recorded_ace_error_numbers()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (a LONG, b TEXT(10))", db).Succeeded);

        // 3010: Table already exists (r-q15-ddl-open/dup-create-sql; table names compare without case: r-cat-1/names Tbl/TBL).
        AssertAceError(db, "CREATE TABLE t (x LONG)", 3010);

        // 3380: Duplicate column name in CREATE TABLE and ALTER TABLE ADD COLUMN.
        AssertAceError(db, "CREATE TABLE U (a LONG, A TEXT(5))", 3380);
        AssertAceError(db, "ALTER TABLE T ADD COLUMN a LONG", 3380);

        // 3382: TEXT(256) out of range (r-def-expr-sql/ddl-name-and-size-limits).
        AssertAceError(db, "CREATE TABLE U (s TEXT(256))", 3382);

        // 3190: > 255 columns.
        string cols256 = string.Join(", ", Enumerable.Range(1, 256).Select(i => $"c{i:D3} LONG"));
        AssertAceError(db, $"CREATE TABLE TooWide ({cols256})", 3190);

        // 3283: Two PRIMARY KEY constraints.
        AssertAceError(db, "CREATE TABLE TwoPk (a LONG PRIMARY KEY, b LONG PRIMARY KEY)", 3283);

        // 3277: > 10 columns in one index (r-def-expr-sql/ddl-name-and-size-limits).
        string idxCols = string.Join(", ", Enumerable.Range(1, 11).Select(i => $"c{i:D2}"));
        string tblCols = string.Join(", ", Enumerable.Range(1, 11).Select(i => $"c{i:D2} LONG"));
        Assert.True(ActionExecutor.Execute($"CREATE TABLE Wide ({tblCols})", db).Succeeded);
        AssertAceError(db, $"CREATE INDEX IX_W ON Wide ({idxCols})", 3277);

        // 3376: DROP TABLE missing table; 3371: ALTER TABLE missing table (r-def-expr-sql/ddl-drop-and-missing-objects, ddl-alter-column-forms).
        AssertAceError(db, "DROP TABLE NoSuchTable", 3376);
        AssertAceError(db, "ALTER TABLE NoSuchTable ADD COLUMN x LONG", 3371);

        // 3372: DROP INDEX missing index; 3755: ALTER TABLE DROP CONSTRAINT missing constraint (r-def-expr-sql/ddl-drop-and-missing-objects, ddl-alter-column-forms).
        AssertAceError(db, "DROP INDEX NoSuchIdx ON T", 3372);
        AssertAceError(db, "ALTER TABLE T DROP CONSTRAINT NoSuchCon", 3755);

        // 3381: ALTER TABLE DROP missing column; 3018: ALTER TABLE ALTER missing column (r-def-expr-sql/ddl-alter-column-forms).
        AssertAceError(db, "ALTER TABLE T DROP COLUMN NoSuchCol", 3381);
        AssertAceError(db, "ALTER TABLE T ALTER COLUMN NoSuchCol TEXT(10)", 3018);

        // 3259: ALTER COLUMN to COUNTER on a non-AutoNumber column.
        AssertAceError(db, "ALTER TABLE T ALTER COLUMN a COUNTER(1, 1)", 3259);

        // 3409: CREATE INDEX on missing column (r-def-expr-sql/ddl-index-forms ix8).
        AssertAceError(db, "CREATE INDEX IX_Bad ON T (missing_col)", 3409);

        // 3285: Duplicate column inside one index key (r-def-expr-sql/ddl-index-forms ixg).
        AssertAceError(db, "CREATE INDEX IX_Dup ON T (a, a)", 3285);

        // 3371: CREATE INDEX on missing table (r-def-expr-sql/ddl-index-forms ix9);
        // 3609: FOREIGN KEY targeting a non-unique column (r-def-expr-sql/ddl-fk-actions Fx5).
        AssertAceError(db, "CREATE INDEX IX_Miss ON NoSuchTable (a)", 3371);
        AssertAceError(db, "CREATE TABLE FkNoUk (a LONG REFERENCES T (a))", 3609);

        // 3125: Invalid table / column / index name (dots, bangs, leading space, >64 chars).
        AssertAceError(db, "CREATE TABLE [bad!name] (a LONG)", 3125);
        AssertAceError(db, $"CREATE TABLE [{new string('a', 65)}] (a LONG)", 3125);

        // 3280: Cannot drop a column that is part of an index.
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_T_A ON T (a)", db).Succeeded);
        AssertAceError(db, "ALTER TABLE T DROP COLUMN a", 3280);

        // 3375: Duplicate index name on same table.
        AssertAceError(db, "CREATE INDEX IX_T_A ON T (b)", 3375);
    }

    [Fact]
    public void Ddl_binder_preserves_declaration_order_and_refuses_open_shapes_by_name()
    {
        using var db = new WritableSqlFixture(EmptyAccdbSha);

        // r-q15-ddl-open/columns-z-a: the binder keeps declaration order, and SchemaWriter writes the column blocks in it
        // (page 42: z then a, PhysicalNumber and ColumnId 0 and 1, VariableIndex 0, FixedOffset 0 and 4, Long, Length 4,
        // fixed, TypeInfo 09 04 00 00).
        DdlStatement? parsed = DdlParser.Parse("CREATE TABLE ZFirst (z LONG, a LONG)", out SqlFailure? pf);
        Assert.Null(pf);
        BoundDdl? bound = DdlBinder.Bind(parsed!, db, out SqlFailure? bf);
        Assert.Null(bf);
        Assert.NotNull(bound);
        Assert.Equal(BoundDdlKind.CreateTable, bound!.Kind);
        Assert.NotNull(bound.CreateTable);
        Assert.Equal(["z", "a"], bound.CreateTable!.Fields.Select(f => f.Name).ToArray());

        ActionOutcome executed = ActionExecutor.Execute("CREATE TABLE ZFirst (z LONG, a LONG)", db);
        Assert.Null(executed.Failure);
        Assert.True(executed.Succeeded);
        IReadOnlyList<ColumnDescriptor> columns = db.Table("ZFirst")!.Columns;
        Assert.Equal(["z", "a"], columns.Select(c => c.Name).ToArray());
        Assert.Equal([0, 1], columns.Select(c => c.PhysicalNumber).ToArray());
        Assert.Equal([0, 1], columns.Select(c => c.ColumnId).ToArray());
        Assert.Equal([0, 0], columns.Select(c => c.VariableIndex).ToArray());
        Assert.Equal([0, 4], columns.Select(c => c.FixedOffset).ToArray());
        Assert.All(columns, c =>
        {
            Assert.Equal(NativeTypes.Long, c.TypeCode);
            Assert.Equal(4, c.Length);
            Assert.True(c.IsFixed);
            Assert.Equal(0x409u, c.TypeInfo);
        });

        // r-q34-ddl-sweep/nn-*: NOT NULL is admitted on LONG, TEXT, MEMO (not on PK/UNIQUE or unrecorded types,
        // and not when a second LvProp LVAL slot or an index + spilling LvProp would be written in one commit).
        AssertNotModelled(db, "CREATE TABLE NnDbl (a DOUBLE NOT NULL)", "NotModelled.DdlColumnNotNull");
        AssertNotModelled(db, "CREATE TABLE NnPk (a LONG NOT NULL CONSTRAINT PK_NnPk PRIMARY KEY)", "NotModelled.DdlColumnNotNull");
        AssertNotModelled(db, "CREATE TABLE NnSpill2 (aaaaaaaa LONG NOT NULL, b LONG NOT NULL)", "NotModelled.SchemaWriter");
        AssertNotModelled(db, "CREATE TABLE NnThree (a LONG NOT NULL, b LONG NOT NULL, c LONG NOT NULL)", "NotModelled.SchemaWriter");
        AssertNotModelled(db, "CREATE TABLE NnPkSpill (ID LONG CONSTRAINT PK_NnS PRIMARY KEY, SSSSSSSS TEXT(50) NOT NULL)", "NotModelled.SchemaWriter");

        // r-q28-schema-write-open/pair-sql-fe-FE, pair-sql-nfc-nfd: non-ASCII case and NFC/NFD collide under General (3380).
        AssertAceError(db, "CREATE TABLE Fe1 ([Fé] LONG, [FÉ] LONG)", 3380);
        AssertAceError(db, "CREATE TABLE Fe2 ([Fé] LONG, [Fe\u0301] LONG)", 3380);

        // Other named NotModelled.Ddl* refusals for valid DDL shapes outside SchemaWriter's current scope.
        AssertNotModelled(db, "CREATE TABLE Tz (s TEXT(0))", "NotModelled.DdlTextZeroSize");
        AssertNotModelled(db, "CREATE TABLE Nm (n NUMERIC)", "NotModelled.DdlFieldType");
        AssertNotModelled(db, "CREATE TABLE Vb (b VARBINARY)", "NotModelled.DdlVarBinaryDefaultSize");
        AssertNotModelled(db, "CREATE TABLE B0 (b BINARY(0))", "NotModelled.DdlBinarySize");
        AssertNotModelled(db, "CREATE TABLE B511 (b BINARY(511))", "NotModelled.DdlBinarySize");
        AssertNotModelled(db, "CREATE TABLE Vb0 (b VARBINARY(0))", "NotModelled.DdlBinarySize");
        AssertNotModelled(db, "CREATE TABLE Cs1 (a COUNTER(1))", "NotModelled.DdlCounterSingleArg");
        AssertNotModelled(db, "CREATE TABLE Bi (x BIGINT)", "NotModelled.DdlBigIntOrDateTime2");
        AssertNotModelled(db, "CREATE TABLE Un (a LONG UNIQUE)", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqLong (id LONG CONSTRAINT PK_UqL PRIMARY KEY, a LONG CONSTRAINT UX_UqL UNIQUE)", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqBefore (s TEXT(10) CONSTRAINT UX_UqB UNIQUE, id LONG CONSTRAINT PK_UqB PRIMARY KEY)", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqTbl (id LONG CONSTRAINT PK_UqT PRIMARY KEY, s TEXT(10), CONSTRAINT UX_UqT UNIQUE (s))", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqTblPk (id LONG, s TEXT(10) CONSTRAINT UX_UqTP UNIQUE, CONSTRAINT PK_UqTP PRIMARY KEY (id))", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqUnnamed (id LONG CONSTRAINT PK_UqU PRIMARY KEY, s TEXT(10) UNIQUE)", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqDupName (id LONG CONSTRAINT PK_Same PRIMARY KEY, s TEXT(10) CONSTRAINT PK_Same UNIQUE)", "NotModelled.DdlUniqueConstraint");
        AssertNotModelled(db, "CREATE TABLE UqUndecided (id LONG CONSTRAINT [ae] PRIMARY KEY, s TEXT(10) CONSTRAINT [\u00e6] UNIQUE)", "NotModelled.DdlFieldNamePair");
        AssertNotModelled(db, "CREATE TABLE LvalTblPk (id LONG, m MEMO, CONSTRAINT PK_Ltp PRIMARY KEY (id))", "NotModelled.DdlLvalWithInlineConstraint");
        AssertNotModelled(db, "CREATE TABLE LvalBeforePk (m MEMO, id LONG CONSTRAINT PK_Lbp PRIMARY KEY)", "NotModelled.DdlLvalWithInlineConstraint");
        AssertNotModelled(db, "CREATE TABLE LvalBetween (id LONG CONSTRAINT PK_Lb PRIMARY KEY, m MEMO, s TEXT(10) CONSTRAINT UX_Lb UNIQUE)", "NotModelled.DdlLvalWithInlineConstraint");
        AssertNotModelled(db, "CREATE TABLE PkText (s TEXT(10) CONSTRAINT PK_Txt PRIMARY KEY)", "NotModelled.DdlPrimaryKeyShape");
        AssertNotModelled(db, "CREATE TABLE PkMulti (a LONG, b LONG, CONSTRAINT PK_M PRIMARY KEY (a, b))", "NotModelled.DdlPrimaryKeyShape");
        AssertNotModelled(db, "CREATE TABLE PkU (a LONG PRIMARY KEY)", "NotModelled.DdlUnnamedPrimaryKey");

        Assert.True(ActionExecutor.Execute("CREATE TABLE P (a LONG CONSTRAINT PK_P PRIMARY KEY)", db).Succeeded);
        AssertNotModelled(db, "CREATE TABLE Fk (a LONG REFERENCES P (a))", "NotModelled.DdlForeignKey");
        // Lesser narrowings: SQL FK type mismatch and >32 CREATE TABLE constraints refuse by name.
        AssertNotModelled(db, "CREATE TABLE FkType (a TEXT(10) REFERENCES P (a))", "NotModelled.DdlForeignKeyTypeMismatch");
        string uq33 = string.Join(", ", Enumerable.Range(1, 33).Select(i => $"c{i:D2} LONG UNIQUE"));
        AssertNotModelled(db, $"CREATE TABLE C33 ({uq33})", "NotModelled.DdlTooManyTableConstraints");

        Assert.True(ActionExecutor.Execute("CREATE TABLE T (a LONG, b LONG, c LONG, d DOUBLE)", db).Succeeded);
        AssertNotModelled(db, "ALTER TABLE T ADD COLUMN e LONG", "NotModelled.DdlAlterTable");
        AssertNotModelled(db, "CREATE INDEX IX_DI ON T (a) WITH DISALLOW NULL IGNORE NULL", "NotModelled.DdlIndexOptions");
        AssertNotModelled(db, "CREATE INDEX IX_PI ON T (a) WITH PRIMARY IGNORE NULL", "NotModelled.DdlIndexOptions");
        AssertNotModelled(db, "CREATE INDEX IX_3 ON T (a, b, c)", "NotModelled.DdlIndexShape");
        AssertNotModelled(db, "CREATE INDEX IX_D ON T (d)", "NotModelled.DdlIndexKeyType");
        Assert.True(ActionExecutor.Execute("CREATE INDEX IX_T ON T (a)", db).Succeeded);
        AssertNotModelled(db, "DROP INDEX IX_T ON T", "NotModelled.DdlDropIndex");
        // Second index on a table that already has one is refused by SchemaWriter (r-q21 Open).
        AssertNotModelled(db, "CREATE UNIQUE INDEX UX_T ON T (b)", "NotModelled.SchemaWriter");
        // B11: multi-name DROP TABLE refuses by name even when one table is missing.
        AssertNotModelled(db, "DROP TABLE T, NoSuchTable", "NotModelled.DdlMultiDropTable");
        // B10: invalid index name against missing table refuses by name instead of assuming 3125 vs 3371 precedence.
        AssertNotModelled(db, "CREATE INDEX [bad!ix] ON NoSuchTable (a)", "NotModelled.DdlErrorPrecedence");
    }

    [Fact]
    public void Ddl_binder_checks_table_in_use_before_missing_column_or_index()
    {
        using var inner = new WritableSqlFixture(EmptyAccdbSha);
        Assert.True(ActionExecutor.Execute("CREATE TABLE T (a LONG)", inner).Succeeded);
        var locked = new LockedTableDatabase(inner, "T");

        // r-def-cursor-dao/err-drop-missing-index-column: open cursor on T fails 3211 for plain DROP / CREATE UNIQUE INDEX and before 3372/3381.
        AssertAceError(locked, "DROP INDEX NoSuch ON T", 3211);
        AssertAceError(locked, "ALTER TABLE T DROP COLUMN NoSuch", 3211);
        AssertAceError(locked, "ALTER TABLE T DROP COLUMN a", 3211);
        AssertAceError(locked, "DROP TABLE T", 3211);
        AssertAceError(locked, "CREATE UNIQUE INDEX UX_T ON T (a)", 3211);

        // B10: held table meeting any other bind error refuses by name instead of assuming 3211 precedence.
        AssertNotModelled(locked, "CREATE INDEX IX_Miss ON T (missing_col)", "NotModelled.DdlTableInUsePrecedence");
        AssertNotModelled(locked, "CREATE INDEX [bad!ix] ON T (a)", "NotModelled.DdlErrorPrecedence");
        AssertNotModelled(locked, "ALTER TABLE T ADD COLUMN a LONG", "NotModelled.DdlTableInUsePrecedence");
        AssertNotModelled(locked, "ALTER TABLE T ALTER COLUMN NoSuch LONG", "NotModelled.DdlTableInUsePrecedence");
        AssertNotModelled(locked, "ALTER TABLE T ALTER COLUMN a COUNTER(1, 1)", "NotModelled.DdlTableInUsePrecedence");
    }

    [Fact]
    public void R_q28_field_name_pairs_follow_general_sort_order()
    {
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(EmptyAccdbSha);

        // Colliding pairs under General 1033/0 (evidence/r-q28-schema-write-open/pair-sql-*): fail 3380 naming the
        // second column, increment MSysObjects/MSysACEs UniqueEntryCount and write 2 page-0 commits (matching every
        // after-close pageHash and the full file SHA-256).
        (string Question, string First, string Second)[] colliding =
        [
            ("pair-sql-Aa-aA", "Aa", "aA"),
            ("pair-sql-fe-FE", "F\u00e9", "F\u00c9"),
            ("pair-sql-nfc-nfd", "F\u00e9", "Fe\u0301"),
            ("pair-sql-ae-lig", "\u00e6", "ae"),
            ("pair-sql-sz-ss", "\u00df", "ss"),
        ];
        foreach ((string question, string first, string second) in colliding)
        {
            Kept kept = Kept.Of(question, "r-q28-schema-write-open", requireReads: false);
            using var db = new WritableSqlFixture((byte[])fixture.Clone());
            ActionOutcome outcome = ActionExecutor.Execute($"CREATE TABLE T ([{first}] LONG, [{second}] LONG)", db);
            Assert.False(outcome.Succeeded, $"{question}: expected 3380");
            Assert.NotNull(outcome.Failure);
            Assert.False(outcome.Failure!.IsNotModelled);
            Assert.Equal(3380, outcome.Failure.Number);
            Assert.Equal($"Field '{second}' already exists in table 'T'.", outcome.Failure.Message);
            Assert.False(db.Workspace.NeedsCloseCommit(db.Cache));
            byte[] ours = db.Image.Snapshot();
            AssertAsAce(kept, fixture, ours);
            Assert.Equal(kept.FileSha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ours)));
        }

        // Distinct pairs under General 1033/0 (evidence/r-q28-schema-write-open/pair-sql-e-eacute, pair-sql-ta-te,
        // pair-sql-o-oslash): succeed and match ACE's kept pages byte-for-byte and every unkept page's pageHash.
        (string Question, string First, string Second)[] distinct =
        [
            ("pair-sql-e-eacute", "e", "\u00e9"),
            ("pair-sql-ta-te", "T\u00e4", "T\u00e9"),
            ("pair-sql-o-oslash", "o", "\u00f8"),
        ];
        foreach ((string question, string first, string second) in distinct)
        {
            Kept kept = Kept.Of(question, "r-q28-schema-write-open");
            (ulong created, ulong updated) = AceDates(kept.AceImage(fixture), "T");
            var timestamps = new Queue<ulong>([created, updated]);
            using var db = new WritableSqlFixture((byte[])fixture.Clone()) { DateBitsProvider = () => timestamps.Dequeue() };
            ActionOutcome outcome = ActionExecutor.Execute($"CREATE TABLE T ([{first}] LONG, [{second}] LONG)", db);
            Assert.True(outcome.Succeeded, $"{question}: {outcome.Failure}");
            Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
            AssertAsAce(kept, fixture, db.Image.Snapshot());
        }

        // Unrecorded pairs stay refused by name (NotModelled.DdlFieldNamePair) without throwing:
        // full/half-width, hiragana/katakana, trailing-space differences, OrdinalIgnoreCase-vs-General disagreements
        // (ƒ/Ƒ, µ/Μ), two distinct diacritics on one base (ä/á), non-Latin-1 same-length (d/đ), General-ignored
        // code points (U+0218), surrogates, and sort orders other than 1033/0 (including 1033/1).
        using (var db = new WritableSqlFixture((byte[])fixture.Clone()))
        {
            AssertNotModelled(db, "CREATE TABLE W ([A] LONG, [\uFF21] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE K ([\u3042] LONG, [\u30A2] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE S ([a] LONG, [a ] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE F1 ([\u0192] LONG, [\u0191] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE F2 ([\u00B5] LONG, [\u039C] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE D1 ([\u00e4] LONG, [\u00e1] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE D2 ([d] LONG, [\u0111] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE I1 ([a] LONG, [a\u0218] LONG)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE S1 ([a] LONG, [\uD83D\uDE00] LONG)", "NotModelled.DdlFieldNamePair");

            // Constraint, CREATE INDEX, and ALTER TABLE column lookups refuse where FormC+OrdinalIgnoreCase and
            // General 1033/0 disagree (e.g. æ vs ae).
            AssertNotModelled(db, "CREATE TABLE P1 ([\u00e6] LONG, CONSTRAINT PK_P1 PRIMARY KEY (ae))", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE U1 ([\u00e6] LONG, CONSTRAINT UQ_U1 UNIQUE (ae))", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "CREATE TABLE FkS ([\u00e6] LONG CONSTRAINT PK_FkS PRIMARY KEY, b LONG, CONSTRAINT FK_S FOREIGN KEY (b) REFERENCES FkS (ae))", "NotModelled.DdlFieldNamePair");
            Assert.True(ActionExecutor.Execute("CREATE TABLE Ae ([\u00e6] LONG)", db).Succeeded);
            AssertNotModelled(db, "CREATE INDEX IX_Ae ON Ae (ae)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "ALTER TABLE Ae ADD COLUMN ae LONG", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "ALTER TABLE Ae ADD CONSTRAINT UQ_Ae UNIQUE (ae)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "ALTER TABLE Ae ALTER COLUMN ae TEXT(10)", "NotModelled.DdlFieldNamePair");
            AssertNotModelled(db, "ALTER TABLE Ae DROP COLUMN ae", "NotModelled.DdlFieldNamePair");

            var nonGeneral = new AlternateSortDatabase(db, 1036u, null);
            Assert.Null(DdlBinder.Prepare("CREATE TABLE Same ([a] LONG, [a] LONG)", nonGeneral, out SqlFailure? sameFail));
            Assert.Equal(3380, sameFail?.Number);
            AssertNotModelled(nonGeneral, "CREATE TABLE Diff ([a] LONG, [b] LONG)", "NotModelled.DdlFieldNamePair");

            CalcDb.Values.Scalar.TextCollation? generalV1 = CalcDb.Expressions.DatabaseCollation.For(new TextSortOrder(TextSortOrder.GeneralIdentifier, 0, 1));
            Assert.NotNull(generalV1);
            var v1Db = new AlternateSortDatabase(db, 1033u, generalV1);
            Assert.Null(DdlBinder.Prepare("CREATE TABLE SameV1 ([a] LONG, [a] LONG)", v1Db, out SqlFailure? sameV1Fail));
            Assert.Equal(3380, sameV1Fail?.Number);
            AssertNotModelled(v1Db, "CREATE TABLE DiffV1 ([a] LONG, [b] LONG)", "NotModelled.DdlFieldNamePair");
        }
    }

    /// <summary>
    /// All 33 in-scope CREATE TABLE records of <c>evidence/r-q34-ddl-sweep</c> executed through
    /// <see cref="ActionExecutor.Execute"/> (SQL/DAO and ADO routes, ACCDB and Jet 4 MDB).
    /// </summary>
    public static TheoryData<string, bool, bool, string> RQ34CreateTableSqlCases() => new()
    {
        // Jet 4 MDB baseline (2)
        { "create-one-long-mdb-ado", true, true, "CREATE TABLE T (n LONG)" },
        { "create-one-long-mdb-dao", true, false, "CREATE TABLE T (n LONG)" },
        // LVAL + inline constraint (8)
        { "lval-pk-memo-accdb-ado", false, true, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)" },
        { "lval-pk-memo-accdb-dao", false, false, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)" },
        { "lval-pk-memo-mdb-dao", true, false, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(10), M MEMO)" },
        { "lval-pk-ole-accdb-dao", false, false, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, B LONGBINARY)" },
        { "lval-pk-ole-memo-accdb-dao", false, false, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, FBin BINARY(16), FOle LONGBINARY, FMemo MEMO, FLong LONG, FText TEXT(32), FDouble DOUBLE)" },
        { "lval-pk-ole-memo-mdb-dao", true, false, "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, FBin BINARY(16), FOle LONGBINARY, FMemo MEMO, FLong LONG, FText TEXT(32), FDouble DOUBLE)" },
        { "lval-pk-uq-alltypes-accdb-dao", false, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_T_Code UNIQUE, L LONG, D DOUBLE, C CURRENCY, Dt DATETIME, S TEXT(50), M MEMO, B LONGBINARY, Y YESNO, Sh SHORT, Bt BYTE, Sg SINGLE, G GUID)" },
        { "lval-pk-uq-alltypes-mdb-dao", true, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK_T PRIMARY KEY, Code TEXT(10) CONSTRAINT UX_T_Code UNIQUE, L LONG, D DOUBLE, C CURRENCY, Dt DATETIME, S TEXT(50), M MEMO, B LONGBINARY, Y YESNO, Sh SHORT, Bt BYTE, Sg SINGLE, G GUID)" },
        // NOT NULL (15)
        { "nn-long-accdb-ado", false, true, "CREATE TABLE T (n LONG NOT NULL)" },
        { "nn-long-accdb-dao", false, false, "CREATE TABLE T (n LONG NOT NULL)" },
        { "nn-long-mdb-ado", true, true, "CREATE TABLE T (n LONG NOT NULL)" },
        { "nn-long-mdb-dao", true, false, "CREATE TABLE T (n LONG NOT NULL)" },
        { "nn-memo-accdb-ado", false, true, "CREATE TABLE T (M MEMO NOT NULL)" },
        { "nn-memo-accdb-dao", false, false, "CREATE TABLE T (M MEMO NOT NULL)" },
        { "nn-memo-mdb-dao", true, false, "CREATE TABLE T (M MEMO NOT NULL)" },
        { "nn-pk-text-accdb-dao", false, false, "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50) NOT NULL)" },
        { "nn-pk-text-mdb-dao", true, false, "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50) NOT NULL)" },
        { "nn-text-accdb-ado", false, true, "CREATE TABLE T (S TEXT(50) NOT NULL)" },
        { "nn-text-accdb-dao", false, false, "CREATE TABLE T (S TEXT(50) NOT NULL)" },
        { "nn-text-mdb-dao", true, false, "CREATE TABLE T (S TEXT(50) NOT NULL)" },
        { "nn-two-cols-accdb-ado", false, true, "CREATE TABLE T (A LONG NOT NULL, B TEXT(50) NOT NULL)" },
        { "nn-two-cols-accdb-dao", false, false, "CREATE TABLE T (A LONG NOT NULL, B TEXT(50) NOT NULL)" },
        { "nn-two-cols-mdb-dao", true, false, "CREATE TABLE T (A LONG NOT NULL, B TEXT(50) NOT NULL)" },
        // COUNTER PK + MDB LONG PK (8)
        { "pk-counter-accdb-ado", false, true, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY)" },
        { "pk-counter-accdb-dao", false, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY)" },
        { "pk-counter-cols-accdb-ado", false, true, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)" },
        { "pk-counter-cols-accdb-dao", false, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)" },
        { "pk-counter-cols-mdb-dao", true, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)" },
        { "pk-counter-mdb-ado", true, true, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY)" },
        { "pk-counter-mdb-dao", true, false, "CREATE TABLE T (ID COUNTER CONSTRAINT PK PRIMARY KEY)" },
        { "pk-long-mdb-dao", true, false, "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50))" },
    };

    [Theory]
    [MemberData(nameof(RQ34CreateTableSqlCases))]
    public void R_q34_ddl_sweep_create_table_executes_byte_exact(string question, bool isMdb, bool isAdo, string sql) =>
        AssertSqlCreateTableMatchesAce("r-q34-ddl-sweep", question, isMdb ? EmptyMdbSha : EmptyAccdbSha, sql, isAdo ? ActionErrorSource.Ado : ActionErrorSource.Database);

    [Theory]
    [InlineData("lv-sql-notnull-1", 1)]
    [InlineData("lv-sql-notnull-4", 4)]
    [InlineData("lv-sql-notnull-8", 8)]
    [InlineData("lv-sql-notnull-16", 16)]
    [InlineData("lv-sql-notnull-64", 64)]
    public void R_q15_lv_sql_notnull_executes_byte_exact(string question, int nameLength) =>
        AssertSqlCreateTableMatchesAce("r-q15-ddl-open", question, EmptyAccdbSha, $"CREATE TABLE T ({new string('n', nameLength)} LONG NOT NULL)", ActionErrorSource.Database);

    [Fact]
    public void R_q11_mdb_create_one_long_executes_byte_exact() =>
        AssertSqlCreateTableMatchesAce("r-q11-ddl-closure", "mdb-create-one-long", EmptyMdbSha, "CREATE TABLE T (n LONG)", ActionErrorSource.Database);

    private static void AssertSqlCreateTableMatchesAce(string card, string question, string fixtureSha, string sql, ActionErrorSource source)
    {
        Kept kept = Kept.Of(question, card);
        byte[] fixture = ArtifactStore.ForRepository(SqlFixture.Root()).ReadAll(fixtureSha);
        byte[] ace = kept.AceImage(fixture);
        (ulong created, ulong updated) = AceDates(ace, "T");

        var timestamps = new Queue<ulong>([created, updated]);
        using var db = new WritableSqlFixture(fixture);
        db.DateBitsProvider = () => timestamps.Dequeue();

        ActionOutcome outcome = ActionExecutor.Execute(sql, db, source: source);
        Assert.Null(outcome.Failure);
        Assert.True(outcome.Succeeded);
        Assert.Equal(0, outcome.RecordsAffected);
        Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        byte[] ours = db.Image.Snapshot();

        // When 2 NOT NULL columns spill MSysObjects.LvProp from step 1's inline blob to step 2's LVAL page,
        // ACE's vacated inline prefix on the MSysObjects data page holds an 8-byte ghost DateUpdate field
        // (deterministically live DateUpdate + 1 ULP across the retained nn-two-cols-* records).
        List<(long Page, int Start, int Length)>? clockSpans = null;
        if (question.StartsWith("nn-two-cols-", StringComparison.Ordinal))
        {
            PropertyMap firstMap = new(PropertySectionKind.ColumnValues, "A", [new StoredProperty("Required", 0x01, NativeTypes.Boolean, [0x01])]);
            byte[] firstBlob = PropertyBlob.Encode([firstMap]);
            CatalogSnapshot catalog = CatalogBootstrap.Open(db.Cache, D, db.PageCount, out _)!;
            CatalogEntry entry = catalog.Entries.Single(e => e.Name == "T" && e.ParentId == catalog.TablesContainer);
            using PagePin objPin = db.Cache.Pin(entry.Root.Page).Pin!;
            SlotView liveSlot = DataPages.CheckedSlot(objPin.Bytes, entry.Root.Row, D, out _)!.Value;
            int ghostStart = liveSlot.Start - firstBlob.Length;
            int dateUpdateOff = ghostStart + D.Row.FixedBase + catalog.ObjectTable.Columns.Single(c => c.Name == "DateUpdate").FixedOffset;
            ulong aceGhostDateUpdate = BitConverter.ToUInt64(ace, (int)(entry.Root.Page * (ulong)D.PageSize) + dateUpdateOff);
            TimeSpan delta = DateTime.FromOADate(BitConverter.UInt64BitsToDouble(aceGhostDateUpdate)) - DateTime.FromOADate(BitConverter.UInt64BitsToDouble(updated));
            Assert.True(Math.Abs(delta.TotalSeconds) < 5.0, $"{card}/{question}: ghost DateUpdate differed from live DateUpdate by {delta}");
            clockSpans = [(entry.Root.Page, dateUpdateOff, 8)];
        }

        AssertAsAce(kept, fixture, ours, clockSpans);
    }

    private sealed class AlternateSortDatabase(WritableSqlFixture inner, uint lcid, CalcDb.Values.Scalar.TextCollation? databaseOrder) : ISqlActionDatabase
    {
        public CalcDb.Values.Scalar.ExpressionContext Context { get; } =
            new(lcid, CalcDb.Values.Scalar.TextCompareMode.Database, databaseOrder, CalcDb.Values.Scalar.ExpressionUse.Query, inner.Context.TextOrder);
        public bool ReadOnly => inner.ReadOnly;
        public CalcDb.Isam.Transactions.TransactionCoordinator Workspace => inner.Workspace;
        public TableDefinition? Table(string name) => inner.Table(name);
        public bool IsQuery(string name) => inner.IsQuery(name);
        public CalcDb.Isam.RowReader? OpenRows(TableDefinition table, out string? failure) => inner.OpenRows(table, out failure);
        public CalcDb.Isam.IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => inner.OpenIndex(table, index, out failure);
        public CalcDb.Isam.Edit.TableEditContext? EditContext(string tableName, out string? failure) => inner.EditContext(tableName, out failure);
        public CalcDb.Isam.TableCursor? OpenCursor(TableDefinition table, out string? failure) => inner.OpenCursor(table, out failure);
    }

    private static void AssertAceError(ISqlActionDatabase db, string sql, int expectedNumber)
    {
        ActionOutcome outcome = ActionExecutor.Execute(sql, db);
        Assert.False(outcome.Succeeded, $"Expected ACE error {expectedNumber}, but succeeded.");
        Assert.NotNull(outcome.Failure);
        Assert.False(outcome.Failure!.IsNotModelled, $"Expected ACE error {expectedNumber}, got {outcome.Failure}");
        Assert.Equal(expectedNumber, outcome.Failure.Number);
    }

    private static void AssertNotModelled(ISqlActionDatabase db, string sql, string expectedName)
    {
        ActionOutcome outcome = ActionExecutor.Execute(sql, db);
        Assert.False(outcome.Succeeded, $"Expected {expectedName}, but succeeded.");
        Assert.NotNull(outcome.Failure);
        Assert.True(outcome.Failure!.IsNotModelled, $"Expected {expectedName}, got {outcome.Failure}");
        Assert.Equal(expectedName, outcome.Failure.NotModelled);
    }

    private sealed record Kept(IReadOnlyList<(long Offset, byte[] Bytes)> Reads, long Length, string[]? PageHashes, string? FileSha256)
    {
        public static Kept Of(string question, string card = "r-q11-ddl-closure", bool requireReads = true)
        {
            string folder = Path.Combine(SqlFixture.Root(), "evidence", card, question);
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
                if (e.TryGetProperty("label", out JsonElement label) && label.GetString() is "after-close" or "after-ddl" && e.TryGetProperty("files", out JsonElement files))
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

        public IEnumerable<long> Pages => Reads.SelectMany(r => Enumerable.Range(0, r.Bytes.Length / D.PageSize).Select(i => (r.Offset / D.PageSize) + i));

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

    private static (ulong Created, ulong Updated) AceDates(byte[] aceImage, string table)
    {
        using var file = new MemoryDatabaseFile(aceImage, D.PageSize);
        var cache = new PageCache(file, file.Incarnation, D.PageSize, 64);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, file.PageCount, out _)!;
        CatalogEntry entry = catalog.Entries.Single(e => e.Name == table && e.ParentId == catalog.TablesContainer);
        return (entry.DateCreateBits!.Value, entry.DateUpdateBits!.Value);
    }

    private static void AssertAsAce(Kept kept, byte[] fixture, byte[] ours, IReadOnlyList<(long Page, int Start, int Length)>? ignoreSpans = null)
    {
        byte[] ace = kept.AceImage(fixture);
        Assert.Equal(kept.Length, ours.Length);
        HashSet<long> keptPages = [.. kept.Pages];
        List<string> differences = [];
        bool wholeFileVerified = false;
        if (kept.PageHashes is null && kept.FileSha256 is not null)
        {
            byte[] normalized = ours;
            if (ignoreSpans is not null && ignoreSpans.Count > 0)
            {
                normalized = [.. ours];
                foreach ((long p, int start, int length) in ignoreSpans)
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
                    if (mine[i] != theirs[i] && !Ignored(ignoreSpans, page, i))
                    {
                        differences.Add($"page {page} +{i}: ours {mine[i]:X2}, ACE {theirs[i]:X2}");
                    }
                }
            }
            else if (kept.PageHashes is not null && page < kept.PageHashes.Length)
            {
                if (ignoreSpans is not null && ignoreSpans.Any(s => s.Page == page && s.Start == 0 && s.Length == D.PageSize))
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

    private static bool Ignored(IReadOnlyList<(long Page, int Start, int Length)>? spans, long page, int offset)
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

    private sealed class LockedTableDatabase(WritableSqlFixture inner, string lockedTable) : ISqlSchemaDatabase
    {
        public CalcDb.Values.Scalar.ExpressionContext Context => inner.Context;
        public bool ReadOnly => inner.ReadOnly;
        public CalcDb.Isam.Transactions.TransactionCoordinator Workspace => inner.Workspace;
        public ulong PageCount => inner.PageCount;
        public TableDefinition? Table(string name) => inner.Table(name);
        public bool IsQuery(string name) => inner.IsQuery(name);
        public CalcDb.Isam.RowReader? OpenRows(TableDefinition table, out string? failure) => inner.OpenRows(table, out failure);
        public CalcDb.Isam.IndexNavigator? OpenIndex(TableDefinition table, PhysicalIndex index, out string? failure) => inner.OpenIndex(table, index, out failure);
        public CalcDb.Isam.Edit.TableEditContext? EditContext(string tableName, out string? failure) => inner.EditContext(tableName, out failure);
        public CalcDb.Isam.TableCursor? OpenCursor(TableDefinition table, out string? failure) => inner.OpenCursor(table, out failure);
        public SchemaWriter? OpenSchemaWriter(out string? failure) => inner.OpenSchemaWriter(out failure);
        public void OnSchemaPublished(ulong extentPages) => inner.OnSchemaPublished(extentPages);
        public ulong NowDateBits() => inner.NowDateBits();
        public bool IsLinkedTable(string name) => inner.IsLinkedTable(name);
        public bool IsTableInRelationship(string tableName) => inner.IsTableInRelationship(tableName);
        public bool IsColumnInRelationship(string tableName, string columnName) => inner.IsColumnInRelationship(tableName, columnName);
        public bool IsIndexInRelationship(string tableName, string indexName) => inner.IsIndexInRelationship(tableName, indexName);
        public bool HasRelationshipNamed(string tableName, string constraintName) => inner.HasRelationshipNamed(tableName, constraintName);
        public bool IsTableInUse(string tableName) => string.Equals(tableName, lockedTable, StringComparison.OrdinalIgnoreCase);
    }
}
