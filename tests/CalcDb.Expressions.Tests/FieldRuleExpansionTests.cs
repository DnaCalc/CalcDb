using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam.Edit;
using CalcDb.Storage;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit tests for the shared field-level <c>ValidationRule</c> expansion in
/// <see cref="ExpressionRuleBinder.ExpandFieldRule"/> and enforcement admission in
/// <see cref="ExpressionRuleBinder.BindFieldRule"/> (W-08 task 9dvt; qualified.md <c>r-q37</c>).
/// </summary>
public sealed class FieldRuleExpansionTests
{
    private static readonly FormatDescriptor D = FormatDescriptor.Jet4Family;
    private const string Txn1bAccdb = "cde7b712cc54e824851b29bf6e711b875a9018edb83b623b81a13a3798799ddb"; // r-txn-1b-main-accdb.accdb

    [Theory]
    [InlineData("[V] > 0", "V", FieldRuleForm.Direct, "[V] > 0")]
    [InlineData("1/0", "V", FieldRuleForm.Direct, "1/0")]
    [InlineData("0", "V", FieldRuleForm.Literal, "[V] = (0)")]
    [InlineData("'ok'", "S", FieldRuleForm.Literal, "[S] = ('ok')")]
    [InlineData("True", "V", FieldRuleForm.Literal, "[V] = (True)")]
    [InlineData("> 10", "V", FieldRuleForm.SingleImplied, "[V] > 10")]
    [InlineData("Like 'A*'", "S", FieldRuleForm.SingleImplied, "[S] Like 'A*'")]
    [InlineData("Between 1 And 10", "V", FieldRuleForm.SingleImplied, "[V] Between 1 And 10")]
    [InlineData("Not Like 'A*'", "S", FieldRuleForm.SingleImplied, "[S] Not Like 'A*'")]
    [InlineData("Is Not Null", "S", FieldRuleForm.SingleImplied, "[S] Is Not Null")]
    [InlineData("In (1, 2)", "V", FieldRuleForm.SingleImplied, "[V] In (1, 2)")]
    [InlineData("<> 'a' And <> 'b'", "S", FieldRuleForm.AndNotEqualImplied, "[S] <> 'a' And [S] <> 'b'")]
    [InlineData("<> 'foo And bar (1)' And <> 'baz Or qux'", "S", FieldRuleForm.AndNotEqualImplied, "[S] <> 'foo And bar (1)' And [S] <> 'baz Or qux'")]
    [InlineData("<> 'a' And <> 'b'\0", "S", FieldRuleForm.AndNotEqualImplied, "[S] <> 'a' And [S] <> 'b'")]
    public void ExpandFieldRule_classifies_recorded_forms(string text, string column, FieldRuleForm expectedForm, string expectedSource)
    {
        string expanded = ExpressionRuleBinder.ExpandFieldRule(text, column, out FieldRuleForm form);
        Assert.Equal(expectedForm, form);
        Assert.Equal(expectedSource, expanded);
    }

    [Theory]
    [InlineData("<> 'a' Or <> 'b'")]
    [InlineData("> 10 And < 100")]
    [InlineData("(<> 'a') And <> 'b'")]
    [InlineData("(> 10)")]
    [InlineData("[S] <> 'a' And <> 'b'")]
    [InlineData("<> 'a' And [S] <> 'b'")]
    [InlineData("> 10 Or [S] < 0")]
    [InlineData("> 10 And 20")]
    public void ExpandFieldRule_marks_unrecorded_multi_clause_forms(string text)
    {
        ExpressionRuleBinder.ExpandFieldRule(text, "S", out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.UnrecordedMultiClause, form);
    }

    [Theory]
    [InlineData(">", ExpressionMessages.MissingOperator)]
    [InlineData("[V] >", ExpressionMessages.MissingOperator)]
    [InlineData("[V] > (10", ExpressionMessages.MissingParenthesis)]
    [InlineData("[V] > 'unterminated", ExpressionMessages.StringSyntax)]
    [InlineData("> 'unterminated", ExpressionMessages.StringSyntax)]
    public void ExpandFieldRule_preserves_unparsed_syntax_diagnostics(string text, string expectedMessage)
    {
        string source = ExpressionRuleBinder.ExpandFieldRule(text, "V", out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.Unparsed, form);
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        Assert.Null(syntax.Root);
        Assert.Equal(expectedMessage, syntax.Failure?.Message);
    }

    [Fact]
    public void BindFieldRule_refuses_multi_clause_implied_rules_and_binds_single_implied()
    {
        (TableDefinition table, ExpressionContext ctx) = LoadTableR();
        ColumnDescriptor colZ = table.Columns.First(c => c.Name == "Z");
        ColumnDescriptor colV = table.Columns.First(c => c.Name == "V");
        var binder = new ExpressionRuleBinder(ctx);

        using IMutationRule? single = binder.BindFieldRule(table, colZ, "<> 'bad'", out string? singleNotAdmitted);
        Assert.Null(singleNotAdmitted);
        Assert.NotNull(single);

        using IMutationRule? between = binder.BindFieldRule(table, colV, "Between 1 And 10", out string? betweenNotAdmitted);
        Assert.Null(betweenNotAdmitted);
        Assert.NotNull(between);

        using IMutationRule? multi = binder.BindFieldRule(table, colZ, "<> 'a' And <> 'b'", out string? multiNotAdmitted);
        Assert.Null(multi);
        Assert.NotNull(multiNotAdmitted);
        Assert.StartsWith("NotModelled.FieldRuleMultiClause", multiNotAdmitted, StringComparison.Ordinal);

        using IMutationRule? orRule = binder.BindFieldRule(table, colZ, "<> 'a' Or <> 'b'", out string? orNotAdmitted);
        Assert.Null(orRule);
        Assert.NotNull(orNotAdmitted);
        Assert.StartsWith("NotModelled.FieldRuleMultiClause", orNotAdmitted, StringComparison.Ordinal);
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
}
