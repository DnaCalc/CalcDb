using System.Text.Json;

using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Standalone tests for Wave 12 Slot 1 (W-06, W-07 / R-EXPR-1, R-SQL-1, R-SQL-2):
/// 1. guid-select-proj (evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb calls 135-152)
/// 2. r-q53 Part A SQL DML scalar conversions (evidence/r-q53-conv-abspos-subq-loc/conv-sql-ins-upd-accdb, conv-sql-ins-upd-mdb)
/// 3. u8yn (evidence/r-q-expr-sql/union-null-first rs17 calls 262-270)
/// 4. 56cs (evidence/r-q18-statements/ins-div0-fail)
/// 5. 9dnf (ActionExecutor Rollback/Commit ResolutionOutcome propagation)
/// </summary>
public sealed unsafe class SqlConvGuidTests
{
    private const string Ado1bMainSha = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb

    [Fact]
    public void Guid_base_column_projection_matches_field_meta_and_lockwait_and_refuses_unrecorded_shapes()
    {
        var db = new SqlFixture(Ado1bMainSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID, G FROM T ORDER BY ID", db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        Assert.Equal(2, plan!.Columns.Count);
        Assert.Equal("G", plan.Columns[1].Name);
        Assert.Equal(15, plan.Columns[1].FieldType);
        Assert.Equal(16, plan.Columns[1].Size);
        Assert.Equal("T", plan.Columns[1].SourceTable);
        Assert.Equal("G", plan.Columns[1].SourceField);

        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        int rowCount = 0;
        for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
        {
            Assert.True(cur.Read(1, out NativeVariant v, out SqlFailure? readFail), readFail?.ToString());
            Assert.True(v.Type is VarType.Null or VarType.Bstr);
            rowCount++;
        }
        Assert.True(rowCount > 0);

        // Non-null GUID projection from field-meta-and-lockwait-accdb (AllTypes.G = {00112233-4455-6677-8899-AABBCCDDEEFF}):
        // matches calls 135-152: VT_BSTR "{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}" and VT_NULL on row 2.
        byte[] allTypesImage = LoadKeptSnapshot(Path.Combine("r-q53-conv-abspos-subq-loc", "conv-sql-ins-upd-accdb"), "pg1");
        using (var wdb = new WritableSqlFixture(allTypesImage))
        {
            Assert.True(ActionExecutor.Execute("CREATE TABLE GT (ID LONG CONSTRAINT PK_GT PRIMARY KEY, G GUID)", wdb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO GT (ID, G) VALUES (1, '{00112233-4455-6677-8899-AABBCCDDEEFF}')", wdb, options: 128).Succeeded);
            Assert.True(ActionExecutor.Execute("INSERT INTO GT (ID, G) VALUES (2, Null)", wdb, options: 128).Succeeded);
            using BoundSelect? gPlan = BoundSelect.Prepare("SELECT ID, G FROM GT ORDER BY ID", wdb, out SqlFailure? gf);
            Assert.Null(gf);
            using SelectCursor? gCur = SelectCursor.Open(gPlan!, wdb, ResultKind.Snapshot, out SqlFailure? gof);
            Assert.Null(gof);
            Assert.Equal(MoveOutcome.Moved, gCur!.MoveFirst());
            Assert.Equal("{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}", ReadStr(gCur, 1));
            Assert.Equal(MoveOutcome.Moved, gCur.MoveNext());
            Assert.Equal(VarType.Null, ReadVal(gCur, 1).Type);
        }

        // Unrecorded GUID shapes stay refused by name.
        (string Sql, string Expected)[] refused =
        [
            ("SELECT G & 'x' FROM T", "NotModelled.FieldType"),
            ("SELECT G = '{guid {00000000-0000-0000-0000-000000000000}}' FROM T", "NotModelled.FieldType"),
            ("SELECT G Is Null FROM T", "NotModelled.FieldType"),
            ("SELECT ID FROM T ORDER BY G", "NotModelled.FieldType"),
            ("SELECT DISTINCT G FROM T", "NotModelled.DistinctGuid"),
            ("SELECT G FROM T GROUP BY G", "NotModelled.FieldType"),
            ("SELECT G FROM T UNION SELECT G FROM T", "NotModelled.UnionType"),
            ("SELECT G FROM (SELECT G FROM T) AS S", "NotModelled.FieldType"),
            ("SELECT (SELECT G FROM T WHERE ID = 1) FROM T", "NotModelled.FieldType"),
        ];
        foreach ((string sql, string expected) in refused)
        {
            using BoundSelect? bad = BoundSelect.Prepare(sql, db, out SqlFailure? f);
            Assert.Null(bad);
            Assert.NotNull(f);
            Assert.Equal(expected, f!.NotModelled);
        }
    }

    [Theory]
    [InlineData("conv-sql-ins-upd-accdb")]
    [InlineData("conv-sql-ins-upd-mdb")]
    public void Sql_dml_scalar_conversions_match_r_q53_conv_sql_ins_upd(string question)
    {
        byte[] setupImage = LoadKeptSnapshot(Path.Combine("r-q53-conv-abspos-subq-loc", question), "pg4");
        using var db = new WritableSqlFixture(setupImage);

        // Call 24: '$12.50' in en-ZA (lcid 7177) fails 3464.
        ActionOutcome r1 = ActionExecutor.Execute(
            "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (1, '42', '-123', '100000', '3.5', '-12.25', '$12.50', '2026-03-15', 'True', '12.3456', 12345)",
            db, options: 128);
        Assert.False(r1.Succeeded);
        Assert.Equal(3464, r1.Failure!.Number);

        // Call 29: 'False' into BIT fails 3464.
        ActionOutcome r2 = ActionExecutor.Execute(
            "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (2, 2.5, 0.5, 2147483646.5, 1.25, 99.99, 0.00005, 0, 'False', 12.34565, True)",
            db, options: 128);
        Assert.False(r2.Succeeded);
        Assert.Equal(3464, r2.Failure!.Number);

        // Call 34: half-to-even numeric text, Currency rounding, Date -> en-ZA text, Decimal scale truncation.
        ActionOutcome r3 = ActionExecutor.Execute(
            "INSERT INTO TS (ID, B, I, L, S, D, C, DT, Y, DC, T) VALUES (3, '2.5', '0.5', '12.5', 0.5, 0.25, 0.00015, #2026-03-15#, -1, '12.34575', #2026-03-15#)",
            db, options: 128);
        Assert.True(r3.Succeeded, r3.Failure?.ToString());
        Assert.Equal(1, r3.RecordsAffected);

        // Call 39: Null -> Yes/No stores False (VT_BOOL 0).
        ActionOutcome r4 = ActionExecutor.Execute("INSERT INTO TS (ID, Y) VALUES (4, Null)", db, options: 128);
        Assert.True(r4.Succeeded, r4.Failure?.ToString());

        // Call 44: over-width text truncates to TEXT(10).
        ActionOutcome r5 = ActionExecutor.Execute("INSERT INTO TS (ID, T) VALUES (5, '123456789012345')", db, options: 128);
        Assert.True(r5.Succeeded, r5.Failure?.ToString());

        // Calls 59, 64: bad text into Byte -> 3464.
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, B) VALUES (10, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, B) VALUES (11, '')", db, options: 128).Failure!.Number);

        // Calls 69, 74: Byte wraps mod 256 (255.5 -> 256 -> 0, 256 -> 0).
        Assert.True(ActionExecutor.Execute("INSERT INTO TS (ID, B) VALUES (12, 255.5)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TS (ID, B) VALUES (13, 256)", db, options: 128).Succeeded);

        // Calls 79, 84: bad text & VT_DECIMAL overflow (32767.5) into Integer -> 3464;
        // evidence/r-q-wire/int-sql-literal-integer: VT_I4 overflow (32768) into Integer -> 6 (Overflow).
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, I) VALUES (14, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, I) VALUES (15, 32767.5)", db, options: 128).Failure!.Number);
        Assert.Equal(6, ActionExecutor.Execute("INSERT INTO TS (ID, I) VALUES (150, 32768)", db, options: 128).Failure!.Number);

        // Calls 89, 94: bad text & overflow into Long -> 3464.
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, L) VALUES (16, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, L) VALUES (17, 2147483648)", db, options: 128).Failure!.Number);

        // Calls 99, 104: Single bad text -> 3464, Single overflow (1e39) -> 6 (Overflow).
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, S) VALUES (18, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(6, ActionExecutor.Execute("INSERT INTO TS (ID, S) VALUES (19, 1e39)", db, options: 128).Failure!.Number);

        // Calls 109, 114, 119, 124, 129: bad text into Double/Currency/DateTime/YesNo/Decimal -> 3464.
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, D) VALUES (20, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, C) VALUES (21, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, DT) VALUES (22, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, Y) VALUES (23, 'abc')", db, options: 128).Failure!.Number);
        Assert.Equal(3464, ActionExecutor.Execute("INSERT INTO TS (ID, DC) VALUES (24, 'abc')", db, options: 128).Failure!.Number);

        // Call 134: over-precision DECIMAL(18,4) -> 3761.
        Assert.Equal(3761, ActionExecutor.Execute("INSERT INTO TS (ID, DC) VALUES (25, '999999999999999.0')", db, options: 128).Failure!.Number);

        // Boolean -> Text formats True as "-1" and False as "0".
        Assert.True(ActionExecutor.Execute("INSERT INTO TS (ID, T) VALUES (30, True)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO TS (ID, T) VALUES (31, False)", db, options: 128).Succeeded);

        // Verify stored values via SELECT.
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID, B, I, L, S, D, C, DT, Y, CStr(DC) AS DCStr, T FROM TS ORDER BY ID", db, out SqlFailure? sf);
        Assert.Null(sf);
        using SelectCursor? cur = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? of);
        Assert.Null(of);

        // Row ID=3
        Assert.Equal(MoveOutcome.Moved, cur!.MoveFirst());
        Assert.Equal(3, (int)ReadVal(cur, 0).Int64);
        Assert.Equal((byte)2, (byte)ReadVal(cur, 1).Int64);
        Assert.Equal((short)0, (short)ReadVal(cur, 2).Int64);
        Assert.Equal(12, (int)ReadVal(cur, 3).Int64);
        Assert.Equal(0.5f, BitConverter.Int32BitsToSingle((int)ReadVal(cur, 4).Int64));
        Assert.Equal(0.25d, ReadVal(cur, 5).Double);
        Assert.Equal(2L, ReadVal(cur, 6).Int64); // 0.0002 * 10000
        Assert.Equal(46096.0, ReadVal(cur, 7).Double);
        Assert.Equal((short)-1, (short)ReadVal(cur, 8).Int64);
        Assert.Equal("12.3457", ReadStr(cur, 9));
        Assert.Equal("2026/03/15", ReadStr(cur, 10));

        // Row ID=4 (Null -> Yes/No False)
        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(4, (int)ReadVal(cur, 0).Int64);
        Assert.Equal((short)0, (short)ReadVal(cur, 8).Int64);

        // Row ID=5 (truncated text)
        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(5, (int)ReadVal(cur, 0).Int64);
        Assert.Equal("1234567890", ReadStr(cur, 10));

        // Row ID=12 (255.5 -> 0)
        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(12, (int)ReadVal(cur, 0).Int64);
        Assert.Equal((byte)0, (byte)ReadVal(cur, 1).Int64);

        // Row ID=13 (256 -> 0)
        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(13, (int)ReadVal(cur, 0).Int64);
        Assert.Equal((byte)0, (byte)ReadVal(cur, 1).Int64);

        // Row ID=30 (True -> "-1") and ID=31 (False -> "0")
        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(30, (int)ReadVal(cur, 0).Int64);
        Assert.Equal("-1", ReadStr(cur, 10));

        Assert.Equal(MoveOutcome.Moved, cur.MoveNext());
        Assert.Equal(31, (int)ReadVal(cur, 0).Int64);
        Assert.Equal("0", ReadStr(cur, 10));

        Assert.NotEqual(MoveOutcome.Moved, cur.MoveNext());

        // Non-BSTR into GUID in SQL DML is unprobed and stays refused as NotModelled.ActionConversion.
        Assert.True(ActionExecutor.Execute("CREATE TABLE TG (ID LONG CONSTRAINT PK_TG PRIMARY KEY, G GUID)", db, options: 128).Succeeded);
        ActionOutcome badGuid = ActionExecutor.Execute("INSERT INTO TG (ID, G) VALUES (1, 12345)", db, options: 128);
        Assert.False(badGuid.Succeeded);
        Assert.Equal("NotModelled.ActionConversion", badGuid.Failure!.NotModelled);
    }

    [Fact]
    public void Distinct_yes_no_union_with_later_null_literal_matches_r_q_expr_sql_union_null_first_rs17()
    {
        byte[] setupImage = LoadKeptSnapshot(Path.Combine("r-q53-conv-abspos-subq-loc", "conv-sql-ins-upd-accdb"), "pg1");
        using var db = new WritableSqlFixture(setupImage);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Ut (y BIT)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE One (ID LONG)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Ut (y) VALUES (-1)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO One (ID) VALUES (1)", db, options: 128).Succeeded);

        // evidence/r-q-expr-sql/union-null-first rs17 (calls 262-270):
        // Field.Type = 1, Size = 1, rows: VT_BOOL -1 (True), VT_BOOL 0 (False).
        using BoundSelect? plan = BoundSelect.Prepare("SELECT y AS v FROM Ut UNION SELECT Null FROM One", db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        Assert.Single(plan!.Columns);
        Assert.Equal("v", plan.Columns[0].Name);
        Assert.Equal(1, plan.Columns[0].FieldType);
        Assert.Equal(1, plan.Columns[0].Size);

        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var vals = new List<short>();
        for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
        {
            NativeVariant v = ReadVal(cur, 0);
            Assert.Equal(VarType.Bool, v.Type);
            vals.Add((short)v.Int64);
        }
        Assert.Equal([(short)-1, (short)0], vals);

        // Mixed UNION / UNION ALL over Yes/No with later Null stays refused by name.
        using BoundSelect? mixed = BoundSelect.Prepare("SELECT y AS v FROM Ut UNION SELECT Null FROM One UNION ALL SELECT y FROM Ut", db, out SqlFailure? mixedFail);
        Assert.Null(mixed);
        Assert.Equal("NotModelled.UnionType", mixedFail?.NotModelled);
    }

    [Fact]
    public void Insert_select_late_div0_streams_prefix_rows_and_propagates_rollback_resolution_outcome()
    {
        // 56cs (evidence/r-q18-statements/ins-div0-fail) + 9dnf (ActionExecutor Rollback ResolutionOutcome):
        // ActionExecutor streams the prefix rows before row 3's division-by-zero (11), calls
        // RecordStatementConversionFailure(11), and persists RowCount=2, AutoNumber=3, and +3 page-0 commits
        // across Execute + Close per evidence/r-q18-statements/ins-div0-fail.
        byte[] emptyAccdb = LoadKeptSnapshot(Path.Combine("r-q53-conv-abspos-subq-loc", "conv-sql-ins-upd-accdb"), "pg1");
        using var db = new WritableSqlFixture(emptyAccdb);
        Assert.True(ActionExecutor.Execute("CREATE TABLE An (ID COUNTER CONSTRAINT PK PRIMARY KEY, K LONG, S TEXT(50), D DOUBLE)", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("CREATE TABLE Src (N LONG CONSTRAINT PKS PRIMARY KEY, G LONG, K LONG, S TEXT(50))", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src VALUES (9, 3, 31, 'g3-a')", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src VALUES (10, 3, 32, 'g3-b')", db, options: 128).Succeeded);
        Assert.True(ActionExecutor.Execute("INSERT INTO Src VALUES (11, 3, 33, 'g3-c-div0')", db, options: 128).Succeeded);
        if (db.Workspace.NeedsCloseCommit(db.Cache))
        {
            Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        }

        uint anPage = db.Table("An")!.FirstPage;
        ulong counterBefore = FormatDescriptor.Jet4Family.Header.CommitCounter.Read(db.Image.Snapshot().AsSpan(0, FormatDescriptor.Jet4Family.PageSize));
        ActionOutcome div0 = ActionExecutor.Execute(
            "INSERT INTO An (K, S, D) SELECT K, S, 1 / (K - 33) FROM Src WHERE G = 3 ORDER BY N",
            db, options: 128);
        Assert.False(div0.Succeeded);
        Assert.False(div0.Failure!.IsNotModelled, div0.Failure.ToString());
        Assert.Equal(11, div0.Failure.Number);
        if (db.Workspace.NeedsCloseCommit(db.Cache))
        {
            Assert.Equal(MutationOpen.None, db.Workspace.PublishCloseCommit(db.Cache).Open);
        }
        byte[] after = db.Image.Snapshot();
        Assert.Equal(counterBefore + 3UL, FormatDescriptor.Jet4Family.Header.CommitCounter.Read(after.AsSpan(0, FormatDescriptor.Jet4Family.PageSize)));
        ReadOnlySpan<byte> anTdef = after.AsSpan((int)(anPage * FormatDescriptor.Jet4Family.PageSize), FormatDescriptor.Jet4Family.PageSize);
        Assert.Equal(2u, FormatDescriptor.Jet4Family.TableDef.RowCount.Read(anTdef));
        Assert.Equal(3u, FormatDescriptor.Jet4Family.TableDef.AutoNumber.Read(anTdef));
    }

    private static NativeVariant ReadVal(SelectCursor cur, int col)
    {
        Assert.True(cur.Read(col, out NativeVariant v, out SqlFailure? f), f?.ToString());
        return v;
    }

    private static string ReadStr(SelectCursor cur, int col)
    {
        NativeVariant v = ReadVal(cur, col);
        Assert.Equal(VarType.Bstr, v.Type);
        return new string(Bstrs.Chars(v.Pointer));
    }

    private static byte[] LoadKeptSnapshot(string relativeDir, string nativeName)
    {
        string dir = Path.Combine(SqlFixture.Root(), "evidence", relativeDir);
        string recordPath = Directory.GetFiles(dir, "*.a1.json").Order(StringComparer.Ordinal).First();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(recordPath));
        foreach (JsonElement ev in doc.RootElement.GetProperty("capture").GetProperty("events").EnumerateArray())
        {
            if (ev.GetProperty("event").GetString() == "handle"
                && ev.GetProperty("op").GetString() == "read"
                && ev.GetProperty("handle").GetString() == nativeName
                && ev.TryGetProperty("bytes", out JsonElement hex))
            {
                return Convert.FromHexString(hex.GetString()!);
            }
        }
        throw new InvalidOperationException($"No kept snapshot '{nativeName}' found in {recordPath}");
    }
}
