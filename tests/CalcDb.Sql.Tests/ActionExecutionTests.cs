using CalcDb.Catalog;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Targeted unit and integration tests for single-table SQL actions (W-07/actions stretch 1; II-11 §§22–28):
/// parser syntax errors and named boundaries, binder resolution and errors, SQL target conversions, INSERT VALUES,
/// INSERT SELECT (explicit, name-mapped, constant, self-insert), UPDATE (original-row view, key-moving stability,
/// failure disposition with and without dbFailOnError, two-workspace 3188 conflict), DELETE forms, parameterized
/// actions, and saved action QueryDef execution through the catalog read path.
/// </summary>
public sealed class ActionExecutionTests
{
    private const string ActAccdb = "a9991effea1dc52b7f857ef9a05a68573df00212d4f79e3ff2c21c01c8f674cd";
    private const string ErrBaseAccdb = "33192b8c7ea72f4d74393996eedd0b157654218c623c63c99de672149b49e5ed";
    private const string DefSqlAccdb = "c654a4d6142d98748ef099461d0ee4b116a784449653052206edad0c65910b70";
    private const string CatQdAccdb = "54d09662048f566a7c2de5f0128cf3b8891a6e0452d18f60ec7a6f247b552616";
    private const string FxByteAccdb = "53afa2ada2f2b1506bc287fa2dc2a561c269145fe9583bbc55ad02adce4a8f03";
    private const string Fmt2bAccdb = "4f42d3faa7c46ac394eaa6c8a62df0cbec4bbf1af93e84ea7f794fb2b98303be";
    private const string Sort3CorpusMdb = "3e2357003d810aa2065a6ce8ab49034dae83b23885abdeeacca3619087bccc0c";

    private static NativeVariant I4(int v) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)v };

    // ---- Parser & Binder errors and named boundaries -------------------------------------------------------------------

    [Theory]
    [InlineData("BEGIN TRANSACTION", 3129)]
    [InlineData("EXEC sp_help", 3129)]
    [InlineData("SELECT ID FROM T", 3065)]
    [InlineData("INSERT T (ID) VALUES (1)", 3134)]
    [InlineData("INSERT INTO T (ID) VALUES ()", 3134)]
    [InlineData("INSERT INTO T (ID, V) VALUES (1, (SELECT 2 FROM T))", 3067)]
    [InlineData("INSERT INTO T (ID) VALUES (1), (2)", 3137)]
    [InlineData("INSERT INTO T (ID) VALUES (1); EXTRA", 3142)]
    [InlineData("UPDATE T SET (V, W) = (1, 2)", 3144)]
    [InlineData("UPDATE T SET V = (SELECT 1 FROM T)", 3073)]
    [InlineData("UPDATE T SET V = 1 FROM T", 3075)]
    [InlineData("DELETE TOP 1 FROM T", 3130)]
    [InlineData("DELETE FROM T, T2", 3128)]
    [InlineData("DELETE FROM T LIMIT 1", 3131)]
    [InlineData("DELETE FROM T ORDER BY ID", 3137)]
    public void Parse_errors_match_recorded_ace_numbers(string sql, int expectedNumber)
    {
        ActionSyntax? syntax = ActionParser.Parse(sql, out SqlFailure? failure);
        Assert.Null(syntax);
        Assert.NotNull(failure);
        Assert.False(failure!.IsNotModelled, failure.ToString());
        Assert.Equal(expectedNumber, failure.Number);
    }

    [Theory]
    [InlineData("CREATE TABLE X (ID LONG)", "NotModelled.DdlStatement")]
    [InlineData("ALTER TABLE T ADD COLUMN Z LONG", "NotModelled.DdlStatement")]
    [InlineData("DROP TABLE T", "NotModelled.DdlStatement")]
    [InlineData("SELECT * INTO TB FROM T", "NotModelled.MakeTableAction")]
    [InlineData("UPDATE T INNER JOIN T2 ON T.ID = T2.ID SET T.V = 1", "NotModelled.JoinedAction")]
    [InlineData("DELETE T.* FROM T INNER JOIN T2 ON T.ID = T2.ID", "NotModelled.JoinedAction")]
    [InlineData("INSERT INTO T IN 'other.accdb' (ID) VALUES (1)", "NotModelled.ExternalDatabaseAction")]
    [InlineData("DELETE FROM T WHERE ID IN (SELECT ID FROM T2)", "NotModelled.Subquery")]
    public void Out_of_scope_actions_return_named_not_modelled(string sql, string expectedName)
    {
        ActionSyntax? syntax = ActionParser.Parse(sql, out SqlFailure? failure);
        Assert.Null(syntax);
        Assert.NotNull(failure);
        Assert.Equal(expectedName, failure!.NotModelled);
    }

    [Theory]
    [InlineData("SELEC ID FROM T", 3078)]
    [InlineData("TRUNCATE TABLE T", 3078)]
    [InlineData("INSERT INTO NoSuchTable (ID) VALUES (1)", 3192)]
    [InlineData("UPDATE NoSuchTable SET V = 1", 3078)]
    [InlineData("DELETE FROM NoSuchTable", 3078)]
    [InlineData("INSERT INTO T (NoCol) VALUES (1)", 3127)]
    [InlineData("INSERT INTO T (ID, ID) VALUES (1, 2)", 3063)]
    [InlineData("INSERT INTO T (ID, V) VALUES (1)", 3346)]
    [InlineData("UPDATE T SET V = 1, V = 2", 3063)]
    [InlineData("UPDATE T SET V = pVal WHERE ID = 1", 3061)]
    public void Bind_and_parameter_errors_match_recorded_ace_numbers(string sql, int expectedNumber)
    {
        using var db = new WritableSqlFixture(ActAccdb);
        ActionOutcome outcome = ActionExecutor.Execute(sql, db, options: 128);
        Assert.False(outcome.Succeeded);
        Assert.Equal(0, outcome.RecordsAffected);
        Assert.NotNull(outcome.Failure);
        Assert.False(outcome.Failure!.IsNotModelled, outcome.Failure.ToString());
        Assert.Equal(expectedNumber, outcome.Failure.Number);
    }

    // ---- INSERT INTO ... VALUES ----------------------------------------------------------------------------------------

    [Fact]
    public void Insert_values_byte_wraps_mod_256_and_long_text_mismatch_fails_3464()
    {
        // r-fix-cursor-dao/ovf-insert-byte-failonerror: 300 into Byte wraps mod 256 to 44.
        using (var fxDb = new WritableSqlFixture(FxByteAccdb))
        {
            ActionOutcome byteWrap = ActionExecutor.Execute("UPDATE Fx SET Fb = 300 WHERE ID = 5", fxDb, options: 128);
            Assert.True(byteWrap.Succeeded, byteWrap.Failure?.ToString());
            Assert.Equal(1, byteWrap.RecordsAffected);
            Assert.Equal("44", fxDb.ReadSelect("SELECT Fb FROM Fx WHERE ID = 5")[0][0].Integer);
        }

        // r-fix-cursor-dao/sql-insert-text-into-number-failonerror: 'abc' into Long fails 3464 and inserts nothing.
        using (var actDb = new WritableSqlFixture(ActAccdb))
        {
            ActionOutcome badNum = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (15, 'abc', 115, 's', 20, 'r')", actDb, options: 128);
            Assert.False(badNum.Succeeded);
            Assert.Equal(3464, badNum.Failure!.Number);
            Assert.Equal(0, badNum.RecordsAffected);
            Assert.Empty(actDb.ReadSelect("SELECT ID FROM T WHERE ID = 15"));
        }

        // r-fix-cursor-dao/ovf-insert-byte-failonerror: parent INSERT on a relationship table clears RI, wraps Bt
        // 300 mod 256 to 44, and materializes omitted Yes/No column Flg as False.
        using (var relDb = new WritableSqlFixture(ErrBaseAccdb))
        {
            ActionOutcome omittedFlg = ActionExecutor.Execute("INSERT INTO P (ID, Code, Req, Bt) VALUES (16, 'x16', 'r', 300)", relDb, options: 128);
            Assert.True(omittedFlg.Succeeded, omittedFlg.Failure?.ToString());
            Assert.Equal(1, omittedFlg.RecordsAffected);
            var insertedRow = relDb.ReadSelect("SELECT Bt, Flg FROM P WHERE ID = 16");
            Assert.Single(insertedRow);
            Assert.Equal("44", insertedRow[0][0].Integer);
            Assert.Equal("VT_BOOL", insertedRow[0][1].VtName);
            Assert.Equal("0", insertedRow[0][1].Integer);

            // r-fix-cursor-dao/con-fk-orphan-insert-failonerror: orphan child INSERT fails 3201 naming parent table 'P'.
            ActionOutcome orphan = ActionExecutor.Execute("INSERT INTO Ch (CID, PID) VALUES (10, 999)", relDb, options: 128);
            Assert.False(orphan.Succeeded);
            Assert.Equal(3201, orphan.Failure!.Number);
            Assert.Equal("You cannot add or change a record because a related record is required in table 'P'.", orphan.Failure.Message);
            Assert.Equal(0, orphan.RecordsAffected);
        }
    }

    [Fact]
    public void Insert_values_long_overflow_3464_and_text_truncation_and_number_to_text()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // r-def-expr-sql/sql-insert-values-forms: Long overflow -> 3464.
        ActionOutcome ovf = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (901, 2147483648, 901, 'a', 20, 'r')", db, options: 128);
        Assert.False(ovf.Succeeded);
        Assert.Equal(3464, ovf.Failure!.Number);

        // r-def-expr-sql/sql-insert-values-forms: number into Text converts via CStr; >10 chars into Text(10) truncates cleanly.
        ActionOutcome numToText = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (902, 1, 902, 123, 20, 'r')", db, options: 128);
        Assert.True(numToText.Succeeded, numToText.Failure?.ToString());
        Assert.Equal("123", db.ReadSelect("SELECT S FROM T WHERE ID = 902")[0][0].Text);

        ActionOutcome trunc = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (903, 1, 903, '0123456789ABCDEF', 20, 'r')", db, options: 128);
        Assert.True(trunc.Succeeded, trunc.Failure?.ToString());
        Assert.Equal("0123456789", db.ReadSelect("SELECT S FROM T WHERE ID = 903")[0][0].Text);
    }

    [Fact]
    public void Insert_values_constraints_with_and_without_fail_on_error()
    {
        using var actDb = new WritableSqlFixture(ActAccdb);

        // Duplicate PK (3022) with dbFailOnError fails; without dbFailOnError succeeds with RecordsAffected 0.
        ActionOutcome dupFail = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (1, 10, 999, 'a', 20, 'r')", actDb, options: 128);
        Assert.False(dupFail.Succeeded);
        Assert.Equal(3022, dupFail.Failure!.Number);
        Assert.Equal(0, dupFail.RecordsAffected);

        ActionOutcome dupSkip = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (1, 10, 999, 'a', 20, 'r')", actDb, options: 0);
        Assert.True(dupSkip.Succeeded, dupSkip.Failure?.ToString());
        Assert.Equal(0, dupSkip.RecordsAffected);

        // Required omitted (3314) on R in ActAccdb.
        Assert.Equal(3314, ActionExecutor.Execute("INSERT INTO R (D) VALUES (5)", actDb, options: 128).Failure!.Number);

        // Zero-length text on columns storing no AllowZeroLength (SQL DDL columns) is admitted, Required False and True
        // alike; stored AllowZeroLength=False fails 3315 (evidence/r-cat-1b/field-zerolength-effect: TP.ZlOff at seq 9,
        // 3315 at seq 13; evidence/r-fix-cursor-dao/con-zero-length-required-failonerror).
        // TP in r-cat-1b-props.accdb also has DateF.DefaultValue=Date(), stopping at NotModelled.Edit.Default until host clock exists.
        ActionOutcome emptyTextUnset = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) VALUES (100, 1, 100, '', 20, 'r')", actDb, options: 128);
        Assert.True(emptyTextUnset.Succeeded, emptyTextUnset.Failure?.ToString());
        Assert.Equal(1, emptyTextUnset.RecordsAffected);
        // R.Z (Required False) takes '' (evidence/r-sql-2-3/act-ins-defaults seq 11: RecordsAffected 1 at seq 12, no error
        // at seq 14; the row reads Req 'c', D Null, Z "" at seq 153-159).
        ActionOutcome emptyZ = ActionExecutor.Execute("INSERT INTO R (Req, D, Z) VALUES ('c', Null, '')", actDb, options: 128);
        Assert.True(emptyZ.Succeeded, emptyZ.Failure?.ToString());
        Assert.Equal(1, emptyZ.RecordsAffected);
        var rowZ = Assert.Single(actDb.ReadSelect("SELECT D, Z FROM R WHERE Req = 'c'"));
        Assert.Equal("VT_NULL", rowZ[0].VtName);
        Assert.Equal("VT_BSTR", rowZ[1].VtName);
        Assert.Equal("", rowZ[1].Text);
        // R.Req (Required True) takes '' (act-ins-defaults seq 31: RecordsAffected 1 at seq 32; the row reads Req "", D 5
        // from its default, Z Null at seq 170-176).
        ActionOutcome emptyReq = ActionExecutor.Execute("INSERT INTO R (Req) VALUES ('')", actDb, options: 128);
        Assert.True(emptyReq.Succeeded, emptyReq.Failure?.ToString());
        Assert.Equal(1, emptyReq.RecordsAffected);
        var rowReq = Assert.Single(actDb.ReadSelect("SELECT D, Z FROM R WHERE Req = ''"));
        Assert.Equal("5", rowReq[0].Integer);
        Assert.Equal("VT_NULL", rowReq[1].VtName);
        using var propsDb = new WritableSqlFixture("be06e19621bcda37f44443957e7765d2e0df92472ccdeca0a2b1697704944cc4");
        Assert.Equal("NotModelled.Edit.Default", ActionExecutor.Execute("INSERT INTO TP (Req, ZlOff) VALUES ('r', '')", propsDb, options: 128).Failure!.NotModelled);

        // Field validation (3316), table validation (3316) on non-related table VR, and stored AllowZeroLength=False (3315) on P.Req.
        using var errDb = new WritableSqlFixture(ErrBaseAccdb);
        Assert.Equal(3316, ActionExecutor.Execute("INSERT INTO VR (ID, Pv, A, B2) VALUES (10, -1, 1, 2)", errDb, options: 128).Failure!.Number);
        Assert.Equal(3316, ActionExecutor.Execute("INSERT INTO VR (ID, Pv, A, B2) VALUES (11, 1, 5, 2)", errDb, options: 128).Failure!.Number);
        Assert.Equal(3315, ActionExecutor.Execute("INSERT INTO P (ID, Req) VALUES (99, '')", errDb, options: 128).Failure!.Number);
    }

    // ---- INSERT INTO ... SELECT ----------------------------------------------------------------------------------------

    [Fact]
    public void Insert_select_constant_and_with_owneraccess_option()
    {
        using var actDb = new WritableSqlFixture(ActAccdb);

        // FROM-less constant SELECT + trailing WITH OWNERACCESS OPTION (r-def-expr-sql/sql-insert-values-forms, r-sql-2-3/act-owneraccess).
        ActionOutcome constIns = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) SELECT 602, 10, 602, 'y', 20, 'r' WITH OWNERACCESS OPTION;", actDb, options: 128);
        Assert.True(constIns.Succeeded, constIns.Failure?.ToString());
        Assert.Equal(1, constIns.RecordsAffected);
        Assert.Equal("y", actDb.ReadSelect("SELECT S FROM T WHERE ID = 602")[0][0].Text);

        // Empty table U in DefSqlAccdb takes a new data page (W-05/growth-structural-closure stretch 3, the r-q16-overflow
        // page rule): the insert is written.
        using var defDb = new WritableSqlFixture(DefSqlAccdb);
        ActionOutcome emptyTarget = ActionExecutor.Execute("INSERT INTO U (ID, K) SELECT 602, 'y' WITH OWNERACCESS OPTION;", defDb, options: 128);
        Assert.True(emptyTarget.Succeeded, emptyTarget.Failure?.ToString());
        Assert.Equal(1, emptyTarget.RecordsAffected);
        Assert.Equal("y", defDb.ReadSelect("SELECT K FROM U WHERE ID = 602")[0][0].Text);
    }

    [Fact]
    public void Insert_select_name_mapping_and_self_insert_finiteness()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // Self-insert from T into T without dbFailOnError (note: T has PK on ID, so ID+100 makes unique rows, and
        // running without dbFailOnError publishes row by row, proving finite materialization without re-entering appended rows).
        int before = db.ReadSelect("SELECT ID FROM T").Count;
        ActionOutcome selfIns = ActionExecutor.Execute("INSERT INTO T (ID, V, U, S, W, R) SELECT ID + 100, V, U + 100, S, W, R FROM T", db, options: 0);
        Assert.True(selfIns.Succeeded, selfIns.Failure?.ToString());
        Assert.Equal(before, selfIns.RecordsAffected);
        Assert.Equal(before * 2, db.ReadSelect("SELECT ID FROM T").Count);
    }

    // ---- UPDATE --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Update_swap_uses_original_row_and_noop_counts_all_matching_rows()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        var before = db.ReadSelect("SELECT ID, V, W FROM T ORDER BY ID");
        // R-SQL-2 ACT-UPD-01: SET V = W, W = V swaps original values.
        ActionOutcome swap = ActionExecutor.Execute("UPDATE T AS d SET d.V = d.W, d.W = d.V WHERE d.ID <= 2", db, options: 128);
        Assert.True(swap.Succeeded, swap.Failure?.ToString());
        Assert.Equal(2, swap.RecordsAffected);
        var after = db.ReadSelect("SELECT ID, V, W FROM T ORDER BY ID");
        Assert.Equal(before[0][2].Integer, after[0][1].Integer);
        Assert.Equal(before[0][1].Integer, after[0][2].Integer);
        Assert.Equal(before[1][2].Integer, after[1][1].Integer);
        Assert.Equal(before[1][1].Integer, after[1][2].Integer);

        // R-SQL-2 ACT-UPD-04: no-op assignment counts every qualifying row.
        ActionOutcome noop = ActionExecutor.Execute("UPDATE T SET V = V", db, options: 128);
        Assert.True(noop.Succeeded, noop.Failure?.ToString());
        Assert.Equal(before.Count, noop.RecordsAffected);
    }

    [Fact]
    public void Update_validation_and_unique_failure_disposition_matches_ace()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // r-sql-2-3/act-fail-validation: T has W = 30, 10, 30, 30 (rule W >= 10).
        // Without dbFailOnError: skips row 2 (10 - 15 = -5), updates 1, 3, 4 -> RecordsAffected = 3.
        ActionOutcome noFlag = ActionExecutor.Execute("UPDATE T SET W = W - 15", db, options: 0);
        Assert.True(noFlag.Succeeded, noFlag.Failure?.ToString());
        Assert.Equal(3, noFlag.RecordsAffected);
        Assert.Equal<string>(["15", "10", "15", "15"], db.ReadSelect("SELECT W FROM T ORDER BY ID").Select(r => r[0].Integer!));

        // With dbFailOnError on T2 (same starting values): updates row 1 (15), stops at row 2 (3316), keeps row 1, leaves 3 & 4 untouched!
        ActionOutcome withFlag = ActionExecutor.Execute("UPDATE T2 SET W = W - 15", db, options: 128);
        Assert.False(withFlag.Succeeded);
        Assert.Equal(3316, withFlag.Failure!.Number);
        Assert.Equal(0, withFlag.RecordsAffected);
        Assert.Equal<string>(["15", "10", "30", "30"], db.ReadSelect("SELECT W FROM T2 ORDER BY ID").Select(r => r[0].Integer!));
    }

    [Fact]
    public void Update_unique_skip_without_flag_and_stop_with_flag()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // r-sql-2-3/act-fail-unique: T has U = 101, 102, 103, 104 (unique).
        ActionOutcome noFlag = ActionExecutor.Execute("UPDATE T SET U = U + 1", db, options: 0);
        Assert.True(noFlag.Succeeded, noFlag.Failure?.ToString());
        Assert.Equal(1, noFlag.RecordsAffected);
        Assert.Equal<string>(["101", "102", "103", "105"], db.ReadSelect("SELECT U FROM T ORDER BY ID").Select(r => r[0].Integer!));

        ActionOutcome withFlag = ActionExecutor.Execute("UPDATE T2 SET U = U + 1", db, options: 128);
        Assert.False(withFlag.Succeeded);
        Assert.Equal(3022, withFlag.Failure!.Number);
        Assert.Equal(0, withFlag.RecordsAffected);
        Assert.Equal<string>(["101", "102", "103", "104"], db.ReadSelect("SELECT U FROM T2 ORDER BY ID").Select(r => r[0].Integer!));
    }

    [Fact]
    public void Update_expression_division_by_zero_stops_keeping_earlier_rows()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // r-txn-1/stmt-expression-fail-d1: 100 / (ID - 3) updates IDs 1 & 2, fails 11 at ID 3, keeps 1 & 2, leaves 3 & 4 untouched.
        var before = db.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
        ActionOutcome fail = ActionExecutor.Execute("UPDATE T SET V = 100 / (ID - 3)", db, options: 128);
        Assert.False(fail.Succeeded);
        Assert.Equal(11, fail.Failure!.Number);
        Assert.Equal(0, fail.RecordsAffected);
        var after = db.ReadSelect("SELECT ID, V FROM T ORDER BY ID");
        Assert.Equal("-50", after[0][1].Integer);
        Assert.Equal("-100", after[1][1].Integer);
        Assert.Equal(before[2][1].Integer, after[2][1].Integer);
        Assert.Equal(before[3][1].Integer, after[3][1].Integer);
    }

    [Fact]
    public void Update_key_moving_stability_visits_each_qualifying_row_once()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // R-SQL-2 ACT-SEL-01/02: T.V is indexed (IX_V_T) with duplicate keys 10, 20, 20, 30.
        // Updating V = V + 100 WHERE V BETWEEN 10 AND 20 visits each qualifying row once without re-entering moved keys.
        ActionOutcome up = ActionExecutor.Execute("UPDATE T SET V = V + 100 WHERE V BETWEEN 10 AND 20", db, options: 128);
        Assert.True(up.Succeeded, up.Failure?.ToString());
        Assert.Equal(3, up.RecordsAffected);
        Assert.Equal<string>(["30", "110", "120", "120"], db.ReadSelect("SELECT V FROM T ORDER BY V").Select(r => r[0].Integer!));
    }

    [Fact]
    public void Update_lock_conflict_from_second_workspace_fails_3188_with_flag_and_skips_without()
    {
        using var db = new WritableSqlFixture(ActAccdb);
        TransactionCoordinator peer = db.CreatePeerWorkspace(2);
        TableDefinition t = db.Table("T")!;
        TableEditContext ctx = db.EditContext("T", out _)!;
        using TableCursor peerCursor = db.OpenCursor(t, out _)!;
        using var peerFrame = new EditFrame(ctx, peerCursor, peer) { LockEdits = true };
        Assert.Equal(MoveOutcome.Moved, peerCursor.MoveFirst());
        Assert.True(peerFrame.Edit().Succeeded);

        // With dbFailOnError -> 3188, RecordsAffected 0.
        ActionOutcome flagged = ActionExecutor.Execute("UPDATE T SET V = V + 1", db, options: 128);
        Assert.False(flagged.Succeeded);
        Assert.Equal(3188, flagged.Failure!.Number);
        Assert.Equal(0, flagged.RecordsAffected);

        // Without dbFailOnError -> succeeds with RecordsAffected 0 (all rows share one page).
        ActionOutcome plain = ActionExecutor.Execute("UPDATE T SET V = V + 1", db, options: 0);
        Assert.True(plain.Succeeded, plain.Failure?.ToString());
        Assert.Equal(0, plain.RecordsAffected);

        peerFrame.CancelUpdate();
    }

    // ---- DELETE --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Delete_forms_and_multi_row_atomic_delete_work()
    {
        using var db = new WritableSqlFixture(ActAccdb);

        // r-def-expr-sql/sql-delete-forms: DELETE *, DELETE alias.*, DELETE col FROM T WHERE ...
        ActionOutcome d1 = ActionExecutor.Execute("DELETE * FROM T WHERE ID = 1", db, options: 128);
        Assert.True(d1.Succeeded, d1.Failure?.ToString());
        Assert.Equal(1, d1.RecordsAffected);

        ActionOutcome d2 = ActionExecutor.Execute("DELETE d.* FROM T AS d WHERE d.ID IN (2, 3)", db, options: 128);
        Assert.True(d2.Succeeded, d2.Failure?.ToString());
        Assert.Equal(2, d2.RecordsAffected);

        // r-def-expr-sql/sql-delete-forms (#65 DELETE ID FROM Dm WHERE ID = 4),
        // r-q17-growth/retire-delete-au1-nine-then-last, r-q20-delete-pages/where-empty-last:
        // row-by-row DELETE ... WHERE of a single data page's last row succeeds and keeps the page owned
        // (never queued for page-1 release at Close).
        ActionOutcome d3 = ActionExecutor.Execute("DELETE ID FROM T WHERE ID = 4", db, options: 128);
        Assert.True(d3.Succeeded, d3.Failure?.ToString());
        Assert.Equal(1, d3.RecordsAffected);
        Assert.Empty(db.ReadSelect("SELECT ID FROM T"));
        Assert.False(db.Workspace.ReleasesAtClose(db.Cache));
    }

    [Fact]
    public void Delete_without_where_uses_delete_every_row_and_refuses_open_cases()
    {
        // evidence/r-q17-growth/retire-delete-au1: DELETE FROM Au1 (without WHERE) takes EditFrame.DeleteEveryRow,
        // removes all 10 rows (RecordsAffected 10), and queues data page 110 for page-1 release at Close.
        using (var db = new WritableSqlFixture(Fmt2bAccdb))
        {
            ActionOutcome del = ActionExecutor.Execute("DELETE FROM Au1", db, options: 128);
            Assert.True(del.Succeeded, del.Failure?.ToString());
            Assert.Equal(10, del.RecordsAffected);
            Assert.Empty(db.ReadSelect("SELECT ID FROM Au1"));
            Assert.True(db.Workspace.ReleasesAtClose(db.Cache));
        }

        // evidence/r-def-expr-sql/sql-delete-forms + evidence/r-q17-growth/retire-delete-au1:
        // single-table column-spec form without WHERE also routes through EditFrame.DeleteEveryRow.
        using (var db = new WritableSqlFixture(Fmt2bAccdb))
        {
            ActionOutcome del = ActionExecutor.Execute("DELETE ID FROM Au1", db, options: 128);
            Assert.True(del.Succeeded, del.Failure?.ToString());
            Assert.Equal(10, del.RecordsAffected);
            Assert.Empty(db.ReadSelect("SELECT ID FROM Au1"));
            Assert.True(db.Workspace.ReleasesAtClose(db.Cache));
        }

        // evidence/r-q17-growth/retire-delete-au1-where-all: DELETE ... WHERE keeps the row-by-row path
        // and leaves the single data page owned (ReleasesAtClose is false).
        using (var db = new WritableSqlFixture(Fmt2bAccdb))
        {
            ActionOutcome del = ActionExecutor.Execute("DELETE FROM Au1 WHERE ID > 0", db, options: 128);
            Assert.True(del.Succeeded, del.Failure?.ToString());
            Assert.Equal(10, del.RecordsAffected);
            Assert.Empty(db.ReadSelect("SELECT ID FROM Au1"));
            Assert.False(db.Workspace.ReleasesAtClose(db.Cache));
        }

        // qualified.md (DELETE without WHERE Open): DELETE without WHERE inside a transaction is refused by name.
        using (var db = new WritableSqlFixture(Fmt2bAccdb))
        {
            Assert.Null(db.Workspace.BeginTrans());
            ActionOutcome inTxn = ActionExecutor.Execute("DELETE FROM Au1", db, options: 128);
            Assert.False(inTxn.Succeeded);
            Assert.Equal("NotModelled.DeleteAllInTransaction", inTxn.Failure!.NotModelled);
            Assert.Equal(0, inTxn.RecordsAffected);
            Assert.Equal(10, db.ReadSelect("SELECT ID FROM Au1").Count);
            db.Workspace.Rollback();
        }

        // qualified.md (DELETE without WHERE Open): DELETE without WHERE on an MDB file is refused by name.
        using (var mdb = new WritableSqlFixture(Sort3CorpusMdb))
        {
            ActionOutcome onMdb = ActionExecutor.Execute("DELETE FROM T", mdb, options: 128);
            Assert.False(onMdb.Succeeded);
            Assert.Equal("NotModelled.DeleteAllMdb", onMdb.Failure!.NotModelled);
            Assert.Equal(0, onMdb.RecordsAffected);
        }
    }

    // ---- Saved Action QueryDef & Parameters ----------------------------------------------------------------------------

    [Fact]
    public void Saved_action_querydef_and_parameterized_action_execute_via_catalog()
    {
        using var db = new WritableSqlFixture(CatQdAccdb);

        // Saved SELECT refuses 3065.
        SavedQuery? selectQuery = db.SavedQuery("QSel");
        if (selectQuery is not null)
        {
            ActionOutcome selExec = db.ExecuteSaved("QSel", options: 128);
            Assert.False(selExec.Succeeded);
            Assert.Equal(3065, selExec.Failure!.Number);
        }

        // r-cat-1b/qd-execute-action-saved: saved QDel (0 rows) and QApp (4 rows into W).
        ActionOutcome qDel = db.ExecuteSaved("QDel", options: 0);
        Assert.True(qDel.Succeeded, qDel.Failure?.ToString());
        Assert.Equal(0, qDel.RecordsAffected);

        ActionOutcome qApp = db.ExecuteSaved("QApp", options: 0);
        Assert.True(qApp.Succeeded, qApp.Failure?.ToString());
        Assert.Equal(4, qApp.RecordsAffected);
        Assert.Equal(5, db.ReadSelect("SELECT ID FROM W").Count);

        // r-cat-1b/qd-execute-param-action: saved QParamUpd without parameter -> 3061; with pV=7 -> 1 row updated.
        ActionOutcome qParamMissing = db.ExecuteSaved("QParamUpd", options: 0);
        Assert.False(qParamMissing.Succeeded);
        Assert.Equal(3061, qParamMissing.Failure!.Number);

        ActionOutcome qParamOk = db.ExecuteSaved("QParamUpd", options: 0, parameters: [I4(7)]);
        Assert.True(qParamOk.Succeeded, qParamOk.Failure?.ToString());
        Assert.Equal(1, qParamOk.RecordsAffected);
        Assert.Equal("7", db.ReadSelect("SELECT B FROM T WHERE ID = 1")[0][0].Integer);

        // Parameterized ad-hoc action executes cleanly when parameter supplied, and fails 3061 when omitted.
        using var actDb = new WritableSqlFixture(ActAccdb);
        const string paramSql = "PARAMETERS pDelta Long, pId Long; UPDATE T SET V = V + pDelta WHERE ID = pId;";
        ActionOutcome missing = ActionExecutor.Execute(paramSql, actDb, options: 128);
        Assert.False(missing.Succeeded);
        Assert.Equal(3061, missing.Failure!.Number);

        ActionOutcome ok = ActionExecutor.Execute(paramSql, actDb, options: 128, parameters: [I4(7), I4(2)]);
        Assert.True(ok.Succeeded, ok.Failure?.ToString());
        Assert.Equal(1, ok.RecordsAffected);
        Assert.Equal("27", actDb.ReadSelect("SELECT V FROM T WHERE ID = 2")[0][0].Integer);
    }

    [Fact]
    public void Refused_pk_duplicate_insert_with_no_TableDef_change_writes_nothing()
    {
        // evidence/r-err-1b/con-pk-duplicate-insert (options 0) and
        // evidence/r-fix-cursor-dao/con-pk-duplicate-insert-failonerror (options 128):
        // before-open and after-close hashes are identical (33192b8c7ea7), proving ACE writes nothing.
        const string sql = "INSERT INTO P (ID, Code, Req) VALUES (1, 'x1', 'r')";

        using (var plainDb = new WritableSqlFixture(ErrBaseAccdb))
        {
            byte[] before = plainDb.Image.Snapshot();
            ActionOutcome res = ActionExecutor.Execute(sql, plainDb, options: 0);
            Assert.True(res.Succeeded, res.Failure?.ToString());
            Assert.Equal(0, res.RecordsAffected);
            Assert.Empty(plainDb.Workspace.Publications);
            Assert.Equal(before, plainDb.Image.Snapshot());
        }

        using (var failDb = new WritableSqlFixture(ErrBaseAccdb))
        {
            byte[] before = failDb.Image.Snapshot();
            ActionOutcome res = ActionExecutor.Execute(sql, failDb, options: 128);
            Assert.False(res.Succeeded);
            Assert.Equal(MutationError.Duplicate, res.Failure!.Number);
            Assert.Equal(0, res.RecordsAffected);
            Assert.Empty(failDb.Workspace.Publications);
            Assert.Equal(before, failDb.Image.Snapshot());
        }
    }

    [Fact]
    public void Rollback_of_sql_insert_without_new_page_persists_autonumber_and_keeps_it_consumed()
    {
        // evidence/r-q27-autonumber-txn/ins-one-existing-txn-rb (and evidence/r-q-fmt-sort/auto-cancel-fail-accdb
        // seq 82-101): BeginTrans, SQL INSERT into an AutoNumber table fitting on the existing page (no new page),
        // Rollback writes only the AutoNumber counter (+1 on page 0, no Close commit), drops the row, and the next
        // insert receives the following AutoNumber.
        using var db = new WritableSqlFixture(Fmt2bAccdb);
        TableDefinition au1 = db.Table("Au1")!;
        int maxBefore = int.Parse(db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer!);
        byte[] before = db.Image.Snapshot();
        byte p0Before = before[FormatDescriptor.Jet4Family.Header.CommitCounter.Offset];

        Assert.Null(db.Workspace.BeginTrans());
        ActionOutcome inTxn = ActionExecutor.Execute("INSERT INTO Au1 (S) VALUES ('in-txn')", db, options: 128);
        Assert.True(inTxn.Succeeded, inTxn.Failure?.ToString());
        Assert.Equal(1, inTxn.RecordsAffected);
        Assert.Equal((maxBefore + 1).ToString(), db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer);

        ResolutionOutcome rb = db.Workspace.Rollback();
        Assert.True(rb.Succeeded, rb.ToString());
        Assert.Single(db.Workspace.Publications);
        Assert.False(db.Workspace.NeedsCloseCommit(db.Cache));
        byte[] afterRb = db.Image.Snapshot();
        Assert.Equal(p0Before + 1, afterRb[FormatDescriptor.Jet4Family.Header.CommitCounter.Offset]);
        Assert.Equal((uint)(maxBefore + 1), (uint)FormatDescriptor.Jet4Family.TableDef.AutoNumber.Read(afterRb.AsSpan((int)(au1.FirstPage * 4096), 4096)));
        Assert.Equal(au1.RowCount, (uint)FormatDescriptor.Jet4Family.TableDef.RowCount.Read(afterRb.AsSpan((int)(au1.FirstPage * 4096), 4096)));
        Assert.Equal(maxBefore.ToString(), db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer);

        ActionOutcome after = ActionExecutor.Execute("INSERT INTO Au1 (S) VALUES ('after-rb')", db, options: 128);
        Assert.True(after.Succeeded, after.Failure?.ToString());
        Assert.Equal(1, after.RecordsAffected);
        Assert.Equal((maxBefore + 2).ToString(), db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer);
    }

    [Fact]
    public void Deleted_record_expression_failure_in_action_statement_refuses_by_name()
    {
        // Task 82kf: BoundExpression.Load emits ACE 3167 for CursorFailure.DeletedRecord, but no record shows a
        // deleted row met during an action statement; ActionExecutor keeps it refused by name rather than surfacing 3167.
        const string sql = "UPDATE T SET V = V + 1";
        using ActionSyntax? parsed = ActionParser.Parse(sql, out SqlFailure? parseFail);
        Assert.Null(parseFail);
        Assert.NotNull(parsed);

        var exprDeleted = ExpressionFailure.Ace(ExpressionPhase.Evaluate, 3167, ExpressionMessages.RecordDeleted);
        SqlFailure fromExpr = ActionExecutor.MapExpressionFailure(exprDeleted, sql, parsed.Assignments[0].Value);
        Assert.True(fromExpr.IsNotModelled);
        Assert.Equal("NotModelled.ActionDeletedRecord", fromExpr.NotModelled);

        var selectDeleted = SqlFailure.Ace(SqlPhase.Read, 3167, ExpressionMessages.RecordDeleted);
        SqlFailure fromSelect = ActionExecutor.MapSelectReadFailure(selectDeleted);
        Assert.True(fromSelect.IsNotModelled);
        Assert.Equal("NotModelled.ActionDeletedRecord", fromSelect.NotModelled);
    }

    [Fact]
    public void Action_executor_refused_failure_aborts_cleanly_at_depth_zero_and_inside_begintrans()
    {
        // Depth 0: multi-row INSERT ... SELECT with AutoNumber failing on row 2 (3022) when caller refuses -> zero writes,
        // byte-identical image, no Close commit, counter untouched, and subsequent accepted INSERT receives maxBefore + 1.
        using (var db = new WritableSqlFixture(Fmt2bAccdb))
        {
            TableDefinition au1 = db.Table("Au1")!;
            int maxBefore = int.Parse(db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer!);
            byte[] before = db.Image.Snapshot();

            // First row inserts cleanly (ID=maxBefore+1), second row collides on PK (ID=1).
            ActionOutcome refusedIns = ActionExecutor.Execute(
                "INSERT INTO Au1 (ID, S) SELECT ID, S FROM Au1 ORDER BY ID",
                db,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedIns.Succeeded);
            Assert.Equal(MutationError.Duplicate, refusedIns.Failure!.Number);
            Assert.Equal(0, db.Workspace.Depth);
            Assert.Empty(db.Workspace.Publications);
            Assert.False(db.Workspace.NeedsCloseCommit(db.Cache));
            Assert.False(db.Workspace.HasGlobalMapActivity(db.Cache));
            Assert.Equal(before, db.Image.Snapshot());
            Assert.Equal(maxBefore, db.EditContext("Au1", out _)!.Counter!.Last);

            // Subsequent accepted INSERT into Au1 receives maxBefore + 1 (aborted statement did not advance AutoNumber).
            ActionOutcome acceptedAfter = ActionExecutor.Execute(
                "INSERT INTO Au1 (S) VALUES ('after-abort')",
                db,
                options: 128);
            Assert.True(acceptedAfter.Succeeded, acceptedAfter.Failure?.ToString());
            int maxAfter = int.Parse(db.ReadSelect("SELECT MAX(ID) FROM Au1")[0][0].Integer!);
            Assert.Equal(maxBefore + 1, maxAfter);

            // Multi-row generated AutoNumber INSERT ... SELECT failing on row 2 via 3464 conversion error when caller refuses:
            // RecordStatementConversionFailure(3464) is skipped, zero writes happen, counter stays 0, and next insert gets ID = 1.
            ActionOutcome createConv = ActionExecutor.Execute(
                "CREATE TABLE AuConv (ID COUNTER CONSTRAINT PK_AuConv PRIMARY KEY, V LONG)",
                db,
                options: 128);
            Assert.True(createConv.Succeeded, createConv.Failure?.ToString());
            if (db.Workspace.NeedsCloseCommit(db.Cache))
            {
                Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
            }
            byte[] beforeConv = db.Image.Snapshot();
            int pubsBeforeConv = db.Workspace.Publications.Count;

            ActionOutcome refusedConv = ActionExecutor.Execute(
                "INSERT INTO AuConv (V) SELECT IIf(ID = 1, 10, 'not-a-num') FROM Au1 ORDER BY ID",
                db,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedConv.Succeeded);
            Assert.Equal(3464, refusedConv.Failure!.Number);
            Assert.Equal(0, db.Workspace.Depth);
            Assert.Equal(pubsBeforeConv, db.Workspace.Publications.Count);
            Assert.False(db.Workspace.NeedsCloseCommit(db.Cache));
            Assert.Equal(beforeConv, db.Image.Snapshot());
            Assert.Equal(0, db.EditContext("AuConv", out _)!.Counter!.Last);

            ActionOutcome acceptedConv = ActionExecutor.Execute(
                "INSERT INTO AuConv (V) VALUES (42)",
                db,
                options: 128);
            Assert.True(acceptedConv.Succeeded, acceptedConv.Failure?.ToString());
            Assert.Equal("1", db.ReadSelect("SELECT ID FROM AuConv")[0][0].Integer);
        }

        // Depth 0: multi-row UPDATE failing row 2 (3316) when caller refuses -> row 1 is NOT published, image byte-identical.
        using (var actDb = new WritableSqlFixture(ActAccdb))
        {
            byte[] before = actDb.Image.Snapshot();
            ActionOutcome refusedUpd = ActionExecutor.Execute(
                "UPDATE T2 SET W = W - 15",
                actDb,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedUpd.Succeeded);
            Assert.Equal(3316, refusedUpd.Failure!.Number);
            Assert.Empty(actDb.Workspace.Publications);
            Assert.Equal(before, actDb.Image.Snapshot());
        }

        // Depth 0: multi-row DELETE with FK 3200 on row 2 when caller refuses -> zero writes, depth 0, image byte-identical.
        using (var relDb = new WritableSqlFixture(ErrBaseAccdb))
        {
            byte[] before = relDb.Image.Snapshot();
            ActionOutcome refusedDel = ActionExecutor.Execute(
                "DELETE FROM P",
                relDb,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedDel.Succeeded);
            Assert.Equal(3200, refusedDel.Failure!.Number);
            Assert.Equal(0, relDb.Workspace.Depth);
            Assert.Empty(relDb.Workspace.Publications);
            Assert.Equal(before, relDb.Image.Snapshot());
        }

        // Inside outer BeginTrans: prior outer edit survives a caller-refused multi-row UPDATE and a caller-refused
        // multi-row INSERT ... SELECT (own frame); outer CommitTrans commits only the prior edit.
        using (var txnDb = new WritableSqlFixture(ActAccdb))
        {
            string origRow1 = txnDb.ReadSelect("SELECT V FROM T WHERE ID = 1")[0][0].Integer!;
            string origT2Row1 = txnDb.ReadSelect("SELECT W FROM T2 WHERE ID = 1")[0][0].Integer!;

            Assert.Null(txnDb.Workspace.BeginTrans());
            ActionOutcome outerEdit = ActionExecutor.Execute("UPDATE T SET V = 77 WHERE ID = 1", txnDb, options: 128);
            Assert.True(outerEdit.Succeeded, outerEdit.Failure?.ToString());

            ActionOutcome refusedUpd = ActionExecutor.Execute(
                "UPDATE T2 SET W = W - 15",
                txnDb,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedUpd.Succeeded);
            Assert.Equal(3316, refusedUpd.Failure!.Number);
            Assert.Equal(1, txnDb.Workspace.Depth);
            Assert.Equal("77", txnDb.ReadSelect("SELECT V FROM T WHERE ID = 1")[0][0].Integer);
            Assert.Equal(origT2Row1, txnDb.ReadSelect("SELECT W FROM T2 WHERE ID = 1")[0][0].Integer);

            ActionOutcome refusedIns = ActionExecutor.Execute(
                "INSERT INTO T (ID, V, U, S, W, R) SELECT ID, V, U, S, W, R FROM T",
                txnDb,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedIns.Succeeded);
            Assert.Equal(MutationError.Duplicate, refusedIns.Failure!.Number);
            Assert.Equal(1, txnDb.Workspace.Depth);

            ResolutionOutcome committed = txnDb.Workspace.CommitTrans();
            Assert.True(committed.Succeeded, committed.ToString());
            Assert.Equal("77", txnDb.ReadSelect("SELECT V FROM T WHERE ID = 1")[0][0].Integer);
            Assert.Equal(origT2Row1, txnDb.ReadSelect("SELECT W FROM T2 WHERE ID = 1")[0][0].Integer);
            Assert.NotEqual("77", origRow1);
        }

        // Inside outer BeginTrans: refused INSERT ... SELECT into an empty AutoNumber table (taking a new page and
        // advancing AutoNumberCounter) restores the counter and discards GlobalMap activity; outer CommitTrans keeps
        // only the outer edit, and a subsequent accepted INSERT into the empty table receives ID = 1.
        using (var txnAutoDb = new WritableSqlFixture(ActAccdb))
        {
            Assert.True(ActionExecutor.Execute(
                "CREATE TABLE AutoNew (ID COUNTER CONSTRAINT PK_AutoNew PRIMARY KEY, K LONG CONSTRAINT UQ_AutoNew UNIQUE)",
                txnAutoDb,
                options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("CREATE TABLE AutoSrc (N LONG, K LONG)", txnAutoDb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO AutoSrc (N, K) VALUES (1, 10)", txnAutoDb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO AutoSrc (N, K) VALUES (2, 10)", txnAutoDb, options: 128).Succeeded);
            if (txnAutoDb.Workspace.NeedsCloseCommit(txnAutoDb.Cache))
            {
                Assert.Equal(MutationOpen.None, txnAutoDb.Workspace.PublishCloseCommit(txnAutoDb.Cache).Open);
            }

            Assert.Null(txnAutoDb.Workspace.BeginTrans());
            Assert.True(ActionExecutor.Execute("UPDATE T SET V = 88 WHERE ID = 1", txnAutoDb, options: 128).Succeeded);

            ActionOutcome refusedNewPage = ActionExecutor.Execute(
                "INSERT INTO AutoNew (K) SELECT K FROM AutoSrc ORDER BY N",
                txnAutoDb,
                options: 128,
                acceptsFailure: _ => false);
            Assert.False(refusedNewPage.Succeeded);
            Assert.Equal(MutationError.Duplicate, refusedNewPage.Failure!.Number);
            Assert.Equal(1, txnAutoDb.Workspace.Depth);
            Assert.Equal(0, txnAutoDb.EditContext("AutoNew", out _)!.Counter!.Last);
            Assert.False(txnAutoDb.Workspace.HasGlobalMapActivity(txnAutoDb.Cache));

            ResolutionOutcome committed = txnAutoDb.Workspace.CommitTrans();
            Assert.True(committed.Succeeded, committed.ToString());
            Assert.False(txnAutoDb.Workspace.NeedsCloseCommit(txnAutoDb.Cache));
            Assert.Equal("88", txnAutoDb.ReadSelect("SELECT V FROM T WHERE ID = 1")[0][0].Integer);

            Assert.True(ActionExecutor.Execute("INSERT INTO AutoNew (K) VALUES (10)", txnAutoDb, options: 128).Succeeded);
            Assert.Equal("1", txnAutoDb.ReadSelect("SELECT ID FROM AutoNew")[0][0].Integer);
        }

        // Accepted failure whose RollbackStatementFrame refuses with a NotModelled limitation (non-1 AutoNumber increment)
        // restores in-memory AutoNumberCounter.Last so the next accepted INSERT does not skip an ID.
        using (var stepDb = new WritableSqlFixture(ActAccdb))
        {
            Assert.True(ActionExecutor.Execute(
                "CREATE TABLE StepAuto (ID COUNTER(100, 5) CONSTRAINT PK_StepAuto PRIMARY KEY, K LONG CONSTRAINT UQ_StepAuto UNIQUE)",
                stepDb,
                options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("CREATE TABLE StepSrc (N LONG, K LONG)", stepDb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO StepSrc (N, K) VALUES (1, 10)", stepDb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO StepSrc (N, K) VALUES (2, 10)", stepDb, options: 128).Succeeded);
            if (stepDb.Workspace.NeedsCloseCommit(stepDb.Cache))
            {
                Assert.Equal(MutationOpen.None, stepDb.Workspace.PublishCloseCommit(stepDb.Cache).Open);
            }
            byte[] beforeStep = stepDb.Image.Snapshot();

            ActionOutcome refusedLimitation = ActionExecutor.Execute(
                "INSERT INTO StepAuto (K) SELECT K FROM StepSrc ORDER BY N",
                stepDb,
                options: 128);
            Assert.False(refusedLimitation.Succeeded);
            Assert.True(refusedLimitation.Failure!.IsNotModelled, refusedLimitation.Failure.ToString());
            Assert.Equal("NotModelled.Edit.CounterWrite", refusedLimitation.Failure.NotModelled);
            Assert.Equal(beforeStep, stepDb.Image.Snapshot());
            Assert.Equal(95, stepDb.EditContext("StepAuto", out _)!.Counter!.Last);

            Assert.True(ActionExecutor.Execute("INSERT INTO StepAuto (K) VALUES (10)", stepDb, options: 128).Succeeded);
            Assert.Equal("100", stepDb.ReadSelect("SELECT ID FROM StepAuto")[0][0].Integer);
        }
    }

    [Fact]
    public void Ddl_executor_refused_duplicate_create_table_writes_nothing()
    {
        using var db = new WritableSqlFixture(ActAccdb);
        byte[] before = db.Image.Snapshot();

        ActionOutcome refused = ActionExecutor.Execute(
            "CREATE TABLE DupCols (A LONG, a LONG)",
            db,
            options: 128,
            source: ActionErrorSource.Ado,
            acceptsFailure: _ => false);

        Assert.False(refused.Succeeded);
        Assert.Equal(3380, refused.Failure!.Number);
        Assert.Empty(db.Workspace.Publications);
        Assert.Equal(before, db.Image.Snapshot());
    }
}
