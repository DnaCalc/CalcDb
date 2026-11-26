using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Values.Windows;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Focused unit tests for expression-layer subquery syntax (<c>Subquery</c>, <c>Exists</c>, <c>InSubquery</c>,
/// <c>QuantifiedComparison</c>, <see cref="SyntaxNode.CloseOffset"/>) and domain function binding
/// (<c>DLookup</c>..<c>DLast</c>) (II-10 §27, II-11 §16; <c>r-q52-ddl-make-domain/domain-*</c>,
/// <c>r-q53-conv-abspos-subq-loc/subq-*</c>).
/// </summary>
public sealed class SubqueryAndDomainSyntaxTests
{
    private static readonly ExpressionContext QueryContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Query,
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(1033) : null);

    private static readonly ExpressionContext FindContext = new(
        1033,
        TextCompareMode.Database,
        DatabaseCollation.For(new TextSortOrder(0x0409, 0, 0)),
        ExpressionUse.Find,
        OperatingSystem.IsWindows() ? WindowsTextCollation.For(1033) : null);

    private sealed class EmptySchema : ExpressionSchema;

    private static readonly EmptySchema Schema = new();

    private static bool DummySubqueryParser(string source, int start, out object? parsed, out int end, out ExpressionFailure? failure)
    {
        int depth = 0;
        int i = start;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                if (depth == 0)
                {
                    break;
                }
                depth--;
            }
            i++;
        }
        parsed = source[start..i].Trim();
        end = i;
        failure = null;
        return true;
    }

    [Fact]
    public void Subquery_syntax_parses_all_four_kinds_with_close_offsets()
    {
        using ExpressionSyntax scalar = ExpressionSyntax.Parse("(SELECT 1 FROM T)", DummySubqueryParser);
        Assert.Null(scalar.Failure);
        Assert.Equal(SyntaxKind.Subquery, scalar.Root!.Kind);
        Assert.Equal("SELECT 1 FROM T", scalar.Root.Subquery);
        Assert.Equal(0, scalar.Root.Offset);
        Assert.Equal(16, scalar.Root.CloseOffset);

        using ExpressionSyntax exists = ExpressionSyntax.Parse("EXISTS (SELECT 1 FROM T)", DummySubqueryParser);
        Assert.Null(exists.Failure);
        Assert.Equal(SyntaxKind.Exists, exists.Root!.Kind);
        Assert.Equal(23, exists.Root.CloseOffset);

        using ExpressionSyntax notIn = ExpressionSyntax.Parse("x NOT IN (SELECT y FROM T)", DummySubqueryParser);
        Assert.Null(notIn.Failure);
        Assert.Equal(SyntaxKind.InSubquery, notIn.Root!.Kind);
        Assert.True(notIn.Root.Negated);
        Assert.Equal(25, notIn.Root.CloseOffset);

        using ExpressionSyntax anyCmp = ExpressionSyntax.Parse("x > ANY (SELECT y FROM T)", DummySubqueryParser);
        Assert.Null(anyCmp.Failure);
        Assert.Equal(SyntaxKind.QuantifiedComparison, anyCmp.Root!.Kind);
        Assert.False(anyCmp.Root.QuantifierAll);
        Assert.Equal(24, anyCmp.Root.CloseOffset);

        using ExpressionSyntax allCmp = ExpressionSyntax.Parse("x <= ALL (SELECT y FROM T)", DummySubqueryParser);
        Assert.Null(allCmp.Failure);
        Assert.Equal(SyntaxKind.QuantifiedComparison, allCmp.Root!.Kind);
        Assert.True(allCmp.Root.QuantifierAll);
        Assert.Equal(25, allCmp.Root.CloseOffset);
    }

    [Fact]
    public void Subquery_without_parser_callback_stays_named_not_modelled()
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse("EXISTS (SELECT 1 FROM T)");
        Assert.Null(syntax.Root);
        Assert.Equal("NotModelled.Subquery", syntax.Failure!.NotModelled);
    }

    [Theory]
    [InlineData("DLookup('Name', 'Items')")]
    [InlineData("DCount('*', 'Items', 'ID > 1')")]
    [InlineData("DSum('Qty', 'Items')")]
    [InlineData("DMin('Qty', 'Items')")]
    [InlineData("DMax('Qty', 'Items')")]
    [InlineData("DAvg('Qty', 'Items')")]
    [InlineData("DFirst('Name', 'Items')")]
    [InlineData("DLast('Name', 'Items')")]
    public void Domain_functions_bind_in_query_mode_and_refuse_find_mode(string expr)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(expr);
        Assert.Null(syntax.Failure);

        using BoundExpression? bound = BoundExpression.Bind(syntax.Root!, Schema, QueryContext, out ExpressionFailure? qFail);
        Assert.Null(qFail);
        Assert.NotNull(bound);
        Assert.True(bound!.UsesDomainFunctions);

        using BoundExpression? findBound = BoundExpression.Bind(syntax.Root!, Schema, FindContext, out ExpressionFailure? fFail);
        Assert.Null(findBound);
        Assert.NotNull(fFail);
        Assert.Equal(3072, fFail!.Number);
    }

    [Theory]
    [InlineData("DLookup('Name')", 14)]
    [InlineData("DLookup('Name', 'Items', 'ID = 1', 'extra')", 42)]
    public void Domain_function_wrong_arity_sets_site_to_closing_paren(string expr, int expectedCloseOffset)
    {
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(expr);
        Assert.Null(syntax.Failure);
        Assert.Equal(expectedCloseOffset, syntax.Root!.CloseOffset);

        using BoundExpression? bound = BoundExpression.Bind(syntax.Root!, Schema, QueryContext, out ExpressionFailure? fail);
        Assert.Null(bound);
        Assert.NotNull(fail);
        Assert.Equal(3075, fail!.Number);
        Assert.Equal(ExpressionMessages.WrongArgumentCount, fail.Message);
        Assert.Equal(expectedCloseOffset, fail.Site);
    }
}
