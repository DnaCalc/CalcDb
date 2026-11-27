using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;

namespace CalcDb.Sql.Tests;

/// <summary>
/// Unit tests for SELECT projecting bare Binary (native type 9) and OLE / Long Binary (native type 11) columns
/// (evidence/r-wire/read-dao, evidence/r-wire/read-sql, evidence/r-wire/ado-getrows-tags,
/// evidence/r-ado-1b/view-sort-grammar, evidence/r-ado-1b/fields-meta-mdb), while refusing expressions and
/// unrecorded clauses over Binary/OLE columns by name.
/// </summary>
public sealed unsafe class BinaryProjectionTests
{
    private const string WireDaoSha = "76ceb0c44e385678689da7e1bdb328c9c25230c28e1c18475ca13e7b01db85b7";  // r-wire-dao.accdb
    private const string Ado1bMainSha = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7"; // r-ado-1b-main.accdb

    [Fact]
    public void Select_projects_bare_binary_column_with_exact_metadata_and_bytes_matching_r_wire_read_dao()
    {
        var db = new SqlFixture(WireDaoSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID, FBin FROM W WHERE ID BETWEEN 1000 AND 1099 ORDER BY ID", db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        Assert.Equal(2, plan!.Columns.Count);
        Assert.Equal("FBin", plan.Columns[1].Name);
        Assert.Equal(9, plan.Columns[1].FieldType);
        Assert.Equal(255, plan.Columns[1].Size);
        Assert.Equal("W", plan.Columns[1].SourceTable);
        Assert.Equal("FBin", plan.Columns[1].SourceField);

        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var rows = new List<(int Id, byte[]? Bytes)>();
        for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
        {
            Assert.True(cur.Read(0, out NativeVariant idVar, out SqlFailure? f0), f0?.ToString());
            Assert.True(cur.Read(1, out NativeVariant binVar, out SqlFailure? f1), f1?.ToString());
            rows.Add(((int)idVar.Int64, ReadBytesOrNull(binVar)));
        }

        // evidence/r-wire/read-dao, evidence/r-wire/ado-getrows-tags: IDs 1000..1007.
        Assert.Equal(8, rows.Count);
        Assert.Equal(1000, rows[0].Id);
        Assert.Null(rows[0].Bytes);
        Assert.Equal(1001, rows[1].Id);
        Assert.Equal(255, rows[1].Bytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x00 }, rows[1].Bytes![..4]);
        Assert.Equal(1002, rows[2].Id);
        Assert.Equal(255, rows[2].Bytes!.Length);
        Assert.All(rows[2].Bytes!, b => Assert.Equal((byte)0, b));
        Assert.Equal(1003, rows[3].Id);
        Assert.Equal(255, rows[3].Bytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, rows[3].Bytes![..4]);
        Assert.Equal(1004, rows[4].Id);
        Assert.Null(rows[4].Bytes);
        Assert.Equal(1005, rows[5].Id);
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, rows[5].Bytes![..4]);
        Assert.Equal(1006, rows[6].Id);
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, rows[6].Bytes![..4]);
        Assert.Equal(1007, rows[7].Id);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, rows[7].Bytes![..4]);
    }

    [Fact]
    public void Select_projects_bare_ole_column_with_exact_metadata_and_bytes_matching_r_wire_read_dao()
    {
        var db = new SqlFixture(WireDaoSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID, FOle FROM W WHERE ID BETWEEN 1100 AND 1199 ORDER BY ID", db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        Assert.Equal(2, plan!.Columns.Count);
        Assert.Equal("FOle", plan.Columns[1].Name);
        Assert.Equal(11, plan.Columns[1].FieldType);
        Assert.Equal(0, plan.Columns[1].Size);
        Assert.Equal("W", plan.Columns[1].SourceTable);
        Assert.Equal("FOle", plan.Columns[1].SourceField);

        using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var rows = new List<(int Id, byte[]? Bytes)>();
        for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
        {
            Assert.True(cur.Read(0, out NativeVariant idVar, out SqlFailure? f0), f0?.ToString());
            Assert.True(cur.Read(1, out NativeVariant oleVar, out SqlFailure? f1), f1?.ToString());
            rows.Add(((int)idVar.Int64, ReadBytesOrNull(oleVar)));
        }

        // evidence/r-wire/read-dao, evidence/r-wire/ado-getrows-tags: IDs 1100..1105.
        Assert.Equal(6, rows.Count);
        Assert.Equal(1100, rows[0].Id);
        Assert.Null(rows[0].Bytes);
        Assert.Equal(1101, rows[1].Id);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x01 }, rows[1].Bytes);
        Assert.Equal(1102, rows[2].Id);
        Assert.Equal(256, rows[2].Bytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x02, 0x03 }, rows[2].Bytes![..4]);
        Assert.Equal(1103, rows[3].Id);
        Assert.Equal(5000, rows[3].Bytes!.Length);
        Assert.Equal(new byte[] { 0x00, 0x07, 0x0E, 0x15 }, rows[3].Bytes![..4]);
        Assert.Equal(1104, rows[4].Id);
        Assert.Equal(new byte[] { 0x41, 0x00, 0x42, 0x00 }, rows[4].Bytes);
        Assert.Equal(1105, rows[5].Id);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, rows[5].Bytes);
    }

    [Fact]
    public void Select_star_and_view_sort_grammar_query_project_ole_column_B_and_handle_tied_order_by()
    {
        var db = new SqlFixture(Ado1bMainSha);

        // evidence/r-ado-1b/view-sort-grammar #16: SELECT ID, L, S, Dt, Y, M, B FROM T.
        using (BoundSelect? plan = BoundSelect.Prepare("SELECT ID, L, S, Dt, Y, M, B FROM T", db, out SqlFailure? f))
        {
            Assert.Null(f);
            Assert.Equal(7, plan!.Columns.Count);
            Assert.Equal("B", plan.Columns[6].Name);
            Assert.Equal(11, plan.Columns[6].FieldType);
            Assert.Equal(0, plan.Columns[6].Size);
            using SelectCursor? cur = SelectCursor.Open(plan, db, ResultKind.Snapshot, out SqlFailure? of);
            Assert.Null(of);
            int count = 0;
            for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
            {
                Assert.True(cur.Read(6, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
                _ = ReadBytesOrNull(v);
                count++;
            }
            Assert.True(count > 0);
        }

        // ORDER BY L has tied rows on T, exercising SelectValues.Copy/Clear and Same on VT_ARRAY|VT_UI1.
        using (BoundSelect? tiedPlan = BoundSelect.Prepare("SELECT ID, L, S, Dt, Y, M, B FROM T ORDER BY L", db, out SqlFailure? tf))
        {
            Assert.Null(tf);
            using SelectCursor? cur = SelectCursor.Open(tiedPlan!, db, ResultKind.Snapshot, out SqlFailure? of);
            Assert.Null(of);
            for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
            {
                Assert.True(cur.Read(6, out NativeVariant v, out SqlFailure? rf), rf?.ToString());
                _ = ReadBytesOrNull(v);
            }
        }

        // T also has a GUID column (native type 15); SELECT * FROM T binds and projects G (guid-select-proj,
        // evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb calls 135-152), while expressions and
        // ORDER BY over G stay refused as NotModelled.FieldType.
        using (BoundSelect? guidStar = BoundSelect.Prepare("SELECT * FROM T", db, out SqlFailure? gf))
        {
            Assert.Null(gf);
            int guidIndex = guidStar!.Columns.ToList().FindIndex(c => c.Name == "G");
            Assert.True(guidIndex >= 0);
            Assert.Equal(15, guidStar.Columns[guidIndex].FieldType);
            Assert.Equal(16, guidStar.Columns[guidIndex].Size);
        }
        using (BoundSelect? guidExpr = BoundSelect.Prepare("SELECT G + 1 FROM T", db, out SqlFailure? gef))
        {
            Assert.Null(guidExpr);
            Assert.Equal("NotModelled.FieldType", gef?.NotModelled);
        }
        using (BoundSelect? guidOrder = BoundSelect.Prepare("SELECT ID FROM T ORDER BY G", db, out SqlFailure? gof))
        {
            Assert.Null(guidOrder);
            Assert.Equal("NotModelled.FieldType", gof?.NotModelled);
        }

        // SELECT * over W (r-wire-dao.accdb: contains FBin type 9 and FOle type 11, no GUID) binds and projects both.
        var wireDb = new SqlFixture(WireDaoSha);
        using (BoundSelect? starPlan = BoundSelect.Prepare("SELECT * FROM W WHERE ID BETWEEN 1000 AND 1199 ORDER BY ID", wireDb, out SqlFailure? sf))
        {
            Assert.Null(sf);
            int binIndex = starPlan!.Columns.ToList().FindIndex(c => c.Name == "FBin");
            int oleIndex = starPlan.Columns.ToList().FindIndex(c => c.Name == "FOle");
            Assert.True(binIndex >= 0);
            Assert.True(oleIndex >= 0);
            Assert.Equal(9, starPlan.Columns[binIndex].FieldType);
            Assert.Equal(255, starPlan.Columns[binIndex].Size);
            Assert.Equal(11, starPlan.Columns[oleIndex].FieldType);
            Assert.Equal(0, starPlan.Columns[oleIndex].Size);
            using SelectCursor? cur = SelectCursor.Open(starPlan, wireDb, ResultKind.Snapshot, out SqlFailure? of);
            Assert.Null(of);
            int count = 0;
            for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
            {
                Assert.True(cur.Read(binIndex, out NativeVariant bv, out SqlFailure? bfail), bfail?.ToString());
                Assert.True(cur.Read(oleIndex, out NativeVariant ov, out SqlFailure? ofail), ofail?.ToString());
                _ = ReadBytesOrNull(bv);
                _ = ReadBytesOrNull(ov);
                count++;
            }
            Assert.Equal(14, count);
        }
    }

    [Fact]
    public void Select_orders_bare_binary_column_ascending_and_descending()
    {
        // evidence/r-q51-codecs-props-compact/bin-desc-keys-accdb, bin-desc-keys-mdb: ORDER BY on bare Binary (native type 9).
        var db = new SqlFixture(WireDaoSha);
        using BoundSelect? plan = BoundSelect.Prepare("SELECT ID, FBin FROM W WHERE ID IN (1000, 1001, 1002, 1003, 1007) ORDER BY FBin DESC", db, out SqlFailure? bindFail);
        Assert.Null(bindFail);
        using SelectCursor? cur = SelectCursor.Open(plan!, db, ResultKind.Snapshot, out SqlFailure? openFail);
        Assert.Null(openFail);
        var ids = new List<int>();
        for (MoveOutcome m = cur!.MoveFirst(); m == MoveOutcome.Moved; m = cur.MoveNext())
        {
            Assert.True(cur.Read(0, out NativeVariant idVar, out SqlFailure? f0), f0?.ToString());
            ids.Add((int)idVar.Int64);
        }
        // 1003 (00 01 02 03), 1007 (00 00 01 02), 1001 (00 00 01 00), 1002 (00 00 00 00), 1000 (Null)
        Assert.Equal([1003, 1007, 1001, 1002, 1000], ids);
    }

    [Theory]
    [InlineData("SELECT FBin & 'x' FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT FOle & 'x' FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT Len(FOle) FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT FBin = FOle FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT FBin Is Null FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT -FBin FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT ID FROM W WHERE FBin Is Null", "NotModelled.FieldType")]
    [InlineData("SELECT ID FROM W WHERE FOle Is Not Null", "NotModelled.FieldType")]
    [InlineData("SELECT ID FROM W ORDER BY FOle", "NotModelled.FieldType")]
    [InlineData("SELECT FBin FROM W GROUP BY FBin", "NotModelled.FieldType")]
    [InlineData("SELECT Count(FBin) FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT Sum(FOle) FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT First(FBin) FROM W", "NotModelled.FieldType")]
    [InlineData("SELECT DISTINCT FBin FROM W", "NotModelled.DistinctLongBinary")]
    [InlineData("SELECT DISTINCT FOle FROM W", "NotModelled.DistinctLongBinary")]
    [InlineData("SELECT FBin FROM W UNION SELECT FBin FROM W", "NotModelled.UnionType")]
    [InlineData("SELECT FOle FROM W UNION ALL SELECT FOle FROM W", "NotModelled.UnionType")]
    [InlineData("SELECT FBin FROM (SELECT FBin FROM W) AS S", "NotModelled.FieldType")]
    [InlineData("SELECT FBin & 'x' FROM (SELECT FBin FROM W) AS S", "NotModelled.FieldType")]
    public void Expressions_and_unrecorded_clauses_over_binary_and_ole_stay_refused_by_name(string sql, string expectedNotModelled)
    {
        var db = new SqlFixture(WireDaoSha);
        using BoundSelect? plan = BoundSelect.Prepare(sql, db, out SqlFailure? failure);
        Assert.Null(plan);
        Assert.NotNull(failure);
        Assert.Equal(expectedNotModelled, failure!.NotModelled);
    }

    private static byte[]? ReadBytesOrNull(in NativeVariant v)
    {
        if (v.Vt == (ushort)VarType.Null)
        {
            return null;
        }
        Assert.Equal((ushort)(VarType.Array | VarType.UI1), v.Vt);
        (int Lower, int Count)[] shape = SafeArrays.Shape(v.Pointer);
        Assert.Single(shape);
        Assert.Equal(0, shape[0].Lower);
        return new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, 0), shape[0].Count).ToArray();
    }
}
