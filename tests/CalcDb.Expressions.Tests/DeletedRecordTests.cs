using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Storage;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Task 82kf (W-06/W-08): <see cref="BoundExpression"/> maps <see cref="CursorFailure.DeletedRecord"/> to ACE 3167
/// ("Record is deleted."; r-txn-1b/rs-types-after-rollback calls 41[0..2], r-def-cursor-dao/delete-by-other-cursor-current-row
/// calls 56, 63) for both non-text (<see cref="IRecordCursor.Read"/>) and text (<see cref="IRecordCursor.ReadText"/>)
/// columns, keeps every other <see cref="CursorFailure"/> as <c>NotModelled.FieldRead</c>, and keeps unrecorded
/// Find/validation-rule deleted-row neighbours refused by name.
/// </summary>
public sealed class DeletedRecordTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string Txn1bAccdb = "cde7b712cc54e824851b29bf6e711b875a9018edb83b623b81a13a3798799ddb"; // r-txn-1b-main-accdb.accdb

    [Theory]
    [InlineData("ID")]
    [InlineData("V")]
    [InlineData("Z")]
    public void DeletedRecord_cursor_failure_returns_ace_3167_for_fixed_and_text_fields(string fieldName)
    {
        (TableDefinition table, ExpressionContext ctx) = LoadTableR();
        using BoundExpression? expr = BoundExpression.Compile(fieldName, new ExpressionSchema(table), ctx, out ExpressionFailure? bindFail);
        Assert.Null(bindFail);
        Assert.NotNull(expr);

        using var cursor = new FailingCursor(table, CursorFailure.DeletedRecord, "row deleted");
        Assert.False(expr.Evaluate(new SingleRow(cursor)));
        Assert.NotNull(expr.LastFailure);
        Assert.False(expr.LastFailure.IsNotModelled);
        Assert.Equal(ExpressionPhase.Evaluate, expr.LastFailure.Phase);
        Assert.Equal(3167, expr.LastFailure.Number);
        Assert.Equal("Record is deleted.", expr.LastFailure.Message);
    }

    [Theory]
    [InlineData("ID", CursorFailure.NoCurrentRecord)]
    [InlineData("Z", CursorFailure.NoCurrentRecord)]
    [InlineData("ID", CursorFailure.Storage)]
    [InlineData("Z", CursorFailure.Storage)]
    [InlineData("ID", CursorFailure.NotObserved)]
    [InlineData("Z", CursorFailure.NotObserved)]
    public void Other_cursor_failures_stay_NotModelled_FieldRead(string fieldName, CursorFailure failure)
    {
        (TableDefinition table, ExpressionContext ctx) = LoadTableR();
        using BoundExpression? expr = BoundExpression.Compile(fieldName, new ExpressionSchema(table), ctx, out ExpressionFailure? bindFail);
        Assert.Null(bindFail);
        Assert.NotNull(expr);

        using var cursor = new FailingCursor(table, failure, "synthetic failure");
        Assert.False(expr.Evaluate(new SingleRow(cursor)));
        Assert.NotNull(expr.LastFailure);
        Assert.True(expr.LastFailure.IsNotModelled);
        Assert.Equal("NotModelled.FieldRead", expr.LastFailure.NotModelled);
    }

    [Fact]
    public void FindCriteria_and_validation_rule_keep_deleted_record_refused_by_name()
    {
        (TableDefinition table, ExpressionContext ctx) = LoadTableR();
        using var cursor = new FailingCursor(table, CursorFailure.DeletedRecord, "row deleted");

        using FindCriteria? criteria = FindCriteria.Bind("ID = 1", new ExpressionSchema(table), ctx, out ExpressionFailure? bindFail);
        Assert.Null(bindFail);
        Assert.NotNull(criteria);
        Assert.Equal(RowTest.Failed, criteria.Test(cursor));
        Assert.True(criteria.LastFailure?.IsNotModelled);
        Assert.Equal("NotModelled.FindDeletedRecord", criteria.LastFailure?.NotModelled);

        var binder = new ExpressionRuleBinder(ctx);
        using IMutationRule? rule = binder.BindFieldRule(table, table.Columns.First(c => c.Name == "V"), "> 0", out string? notAdmitted);
        Assert.Null(notAdmitted);
        Assert.NotNull(rule);
        RuleResult verdict = rule.Evaluate(cursor);
        Assert.Equal(RuleVerdict.NotModelled, verdict.Verdict);
        Assert.Contains("NotModelled.RuleDeletedRecord", verdict.Detail);
    }

    private static (TableDefinition Table, ExpressionContext Context) LoadTableR()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = ArtifactStore.ForRepository(root).ReadAll(Txn1bAccdb);
        var image = new ImagePageSource(file, D.PageSize);
        var cache = new PageCache(image, 1, D.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, D, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(D).First(e => string.Equals(e.Name, "R", StringComparison.OrdinalIgnoreCase));
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p =>
            {
                using PagePin pin = cache.Pin(p).Pin!;
                return pin.Bytes.ToArray();
            }, D, 8, out _)!,
            (uint)entry.Id,
            D,
            out _)!;
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, D.PageSize), D);
        var ctx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
        return (table, ctx);
    }

    private sealed class SingleRow(IRecordCursor cursor) : IExpressionRow
    {
        public IRecordCursor? Source(int source) => source == 0 ? cursor : null;

        public NativeVariant Slot(int slot) => default;
    }

    private sealed class FailingCursor(TableDefinition table, CursorFailure failure, string detail) : IRecordCursor
    {
        public TableDefinition Table => table;

        public bool Bof => false;

        public bool Eof => false;

        public bool NoMatch => false;

        public long RecordCount => 1;

        public MoveOutcome MoveFirst() => MoveOutcome.NotSupported;

        public MoveOutcome MoveLast() => MoveOutcome.NotSupported;

        public MoveOutcome MoveNext() => MoveOutcome.NotSupported;

        public MoveOutcome MovePrevious() => MoveOutcome.NotSupported;

        public MoveOutcome Move(long rows) => MoveOutcome.NotSupported;

        public byte[]? GetBookmark(out CursorFailure? f)
        {
            f = failure;
            return null;
        }

        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => failure;

        public FieldResult Read(ColumnDescriptor column) => new(default, failure, detail);

        public TextFieldRead ReadText(ColumnDescriptor column) => new(new FieldResult(default, failure, detail));

        public void Dispose() { }
    }
}
