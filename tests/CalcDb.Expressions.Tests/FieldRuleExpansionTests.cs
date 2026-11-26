using System.Runtime.InteropServices;
using CalcDb.Catalog;
using CalcDb.Evidence;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Storage;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions.Tests;

/// <summary>
/// Unit and replay tests for the shared field-level <c>ValidationRule</c> expansion in
/// <see cref="ExpressionRuleBinder.ExpandFieldRule"/> and enforcement in
/// <see cref="ExpressionRuleBinder.BindFieldRule"/> (qualified.md <c>R-EXPR-1 EXPR-MUTATION</c>,
/// <c>r-q37</c>, <c>r-q42</c>).
/// </summary>
public sealed class FieldRuleExpansionTests
{
    private const string Txn1bAccdb = "cde7b712cc54e824851b29bf6e711b875a9018edb83b623b81a13a3798799ddb"; // r-txn-1b-main-accdb.accdb
    private const string PlainAccdb = "5bd002eaf9f69ab1719d0ebf7e263e6853327f1c42c82f906966dca4854cb0f1"; // r-q37-plain-accdb.accdb
    private const string PlainMdb = "136bce0e84a9c979a0cb63ef86039e70ecb8114ad27a2ccc22279f2c45ea6645";   // r-q37-plain-mdb.mdb

    [Theory]
    [InlineData("[V] > 0", "V", FieldRuleForm.Direct, "[V] > 0")]
    [InlineData("([V] > 0) And Null", "V", FieldRuleForm.Direct, "([V] > 0) And Null")]
    [InlineData("Null And ([V] > 0)", "V", FieldRuleForm.Direct, "Null And ([V] > 0)")]
    [InlineData("([V] > 0) And True", "V", FieldRuleForm.Direct, "([V] > 0) And True")]
    [InlineData("([Y] = True) Or False", "Y", FieldRuleForm.Direct, "([Y] = True) Or False")]
    [InlineData("Not True", "V", FieldRuleForm.Direct, "Not True")]
    [InlineData("(1 + 2) > 0", "V", FieldRuleForm.Direct, "(1 + 2) > 0")]
    [InlineData("([V] + 1) * 2 > 0", "V", FieldRuleForm.Direct, "([V] + 1) * 2 > 0")]
    [InlineData("(1) = [V]", "V", FieldRuleForm.Direct, "(1) = [V]")]
    [InlineData("(-5) = [V]", "V", FieldRuleForm.Direct, "(-5) = [V]")]
    [InlineData("([V]) > 0", "V", FieldRuleForm.Direct, "([V]) > 0")]
    [InlineData("Not [W] = 'a'", "W", FieldRuleForm.Direct, "Not [W] = 'a'")]
    [InlineData("1/0", "V", FieldRuleForm.Direct, "1/0")]
    [InlineData("0", "V", FieldRuleForm.Literal, "[V] = (0)")]
    [InlineData("'ok'", "S", FieldRuleForm.Literal, "[S] = ('ok')")]
    [InlineData("True", "V", FieldRuleForm.Literal, "[V] = (True)")]
    [InlineData("False", "V", FieldRuleForm.Literal, "[V] = (False)")]
    [InlineData("Null", "V", FieldRuleForm.Literal, "[V] = (Null)")]
    [InlineData("> 10", "V", FieldRuleForm.SingleImplied, "[V] > 10")]
    [InlineData("Like 'A*'", "S", FieldRuleForm.SingleImplied, "[S] Like 'A*'")]
    [InlineData("Between 1 And 10", "V", FieldRuleForm.SingleImplied, "[V] Between 1 And 10")]
    [InlineData("Not Like 'A*'", "S", FieldRuleForm.SingleImplied, "[S] Not Like 'A*'")]
    [InlineData("Is Not Null", "S", FieldRuleForm.SingleImplied, "[S] Is Not Null")]
    [InlineData("In (1, 2)", "V", FieldRuleForm.SingleImplied, "[V] In (1, 2)")]
    [InlineData("<> 'a' = False", "W", FieldRuleForm.SingleImplied, "[W] <> 'a' = False")]
    [InlineData("<> 'a' <> 'b'", "W", FieldRuleForm.SingleImplied, "[W] <> 'a' <> 'b'")]
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
    [InlineData("-5")]
    [InlineData("1 Or -5")]
    [InlineData("Not -5")]
    [InlineData("1 Or (-5)")]
    public void ExpandFieldRule_refuses_unrecorded_bare_negative_numeric_literals(string text)
    {
        ExpressionRuleBinder.ExpandFieldRule(text, "V", out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.UnrecordedMultiClause, form);
    }

    [Theory]
    [InlineData("<> 'a' Or <> 'b'", "W", "[W] <> 'a' Or [W] <> 'b'")]
    [InlineData("> 10 And < 100", "V", "[V] > 10 And [V] < 100")]
    [InlineData("(<> 'a') And <> 'b'", "W", "([W] <> 'a') And [W] <> 'b'")]
    [InlineData("(> 10)", "V", "([V] > 10)")]
    [InlineData("[S] <> 'a' And <> 'b'", "S", "[S] <> 'a' And [S] <> 'b'")]
    [InlineData("<> 'a' And [S] <> 'b'", "S", "[S] <> 'a' And [S] <> 'b'")]
    [InlineData("> 10 Or [V] < 0", "V", "[V] > 10 Or [V] < 0")]
    [InlineData("> 10 And 20", "V", "[V] > 10 And [V] = (20)")]
    [InlineData("Not 'a'", "W", "Not [W] = ('a')")]
    [InlineData("Not = 'a'", "W", "Not [W] = 'a'")]
    [InlineData("Not Not = 'a'", "W", "Not Not [W] = 'a'")]
    [InlineData("Not Is Null", "W", "Not [W] Is Null")]
    [InlineData("Not (= 'a' Or = 'b')", "W", "Not ([W] = 'a' Or [W] = 'b')")]
    [InlineData("= 'a' Xor Like 'a*'", "W", "[W] = 'a' Xor [W] Like 'a*'")]
    [InlineData("> 0 Eqv < 10", "V", "[V] > 0 Eqv [V] < 10")]
    [InlineData("> 0 Imp < 10", "V", "[V] > 0 Imp [V] < 10")]
    [InlineData("Between 1 And 10 And <> 5", "V", "[V] Between 1 And 10 And [V] <> 5")]
    [InlineData("> 'a' And Between 'b' And 'm'", "W", "[W] > 'a' And [W] Between 'b' And 'm'")]
    [InlineData("In (1, 3) Or = 5", "V", "[V] In (1, 3) Or [V] = 5")]
    [InlineData("1 Or 2", "V", "[V] = (1) Or [V] = (2)")]
    [InlineData("And <> 'b'", "W", "([W] = 'And') <> 'b'")]
    [InlineData("And <> 5", "V", "([V] = 'And') <> 5")]
    public void ExpandFieldRule_expands_r_q42_multi_clause_forms(string text, string column, string expectedSource)
    {
        // r-q42-field-rule-and/put-forms-accdb, r-q42-field-rule-and/edge-forms-accdb
        string expanded = ExpressionRuleBinder.ExpandFieldRule(text, column, out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.MultiClause, form);
        Assert.Equal(expectedSource, expanded);
    }

    [Theory]
    [InlineData(">", ExpressionMessages.MissingOperator)]
    [InlineData("[V] >", ExpressionMessages.MissingOperator)]
    [InlineData("[V] > (10", ExpressionMessages.MissingParenthesis)]
    [InlineData("[V] > 'unterminated", ExpressionMessages.StringSyntax)]
    [InlineData("> 'unterminated", ExpressionMessages.StringSyntax)]
    [InlineData("<> 'a' And", ExpressionMessages.MissingOperator)]
    [InlineData("<> 'a' And >", ExpressionMessages.MissingOperator)]
    [InlineData("= 'a' Or", ExpressionMessages.MissingOperator)]
    [InlineData("> 0 And", ExpressionMessages.MissingOperator)]
    [InlineData("> < 5", ExpressionMessages.MissingOperator)]
    [InlineData("<> 'a' And And <> 'b'", ExpressionMessages.MissingOperator)]
    [InlineData("And = 'b'", ExpressionMessages.MissingOperator)]
    [InlineData("And <> 'b' And <> 'c'", ExpressionMessages.MissingOperator)]
    [InlineData("And <> 'b' And [V] <> 'c'", ExpressionMessages.MissingOperator)]
    [InlineData("(<> 'a'", ExpressionMessages.MissingParenthesis)]
    public void ExpandFieldRule_preserves_unparsed_syntax_diagnostics(string text, string expectedMessage)
    {
        // r-q42-field-rule-and/put-refusals-accdb
        string source = ExpressionRuleBinder.ExpandFieldRule(text, "V", out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.Unparsed, form);
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        Assert.Null(syntax.Root);
        Assert.Equal(expectedMessage, syntax.Failure?.Message);
    }

    [Fact]
    public void ExpandFieldRule_preserves_subquery_refusal_for_implied_in()
    {
        // r-q42-field-rule-and/put-refusals-accdb: In (SELECT W FROM T) -> NotModelled.Subquery
        string source = ExpressionRuleBinder.ExpandFieldRule("In (SELECT W FROM T)", "W", out FieldRuleForm form);
        Assert.Equal(FieldRuleForm.Unparsed, form);
        using ExpressionSyntax syntax = ExpressionSyntax.Parse(source);
        Assert.Null(syntax.Root);
        Assert.NotNull(syntax.Failure);
        Assert.Equal("NotModelled.Subquery", syntax.Failure!.NotModelled);
    }

    [Fact]
    public void BindFieldRule_accepts_all_r_q42_put_forms_and_refuses_other_column_and_table_implied()
    {
        // r-q42-field-rule-and/put-forms-accdb, r-q42-field-rule-and/put-refusals-accdb, r-q42-field-rule-and/edge-forms-accdb
        (TableDefinition table, ExpressionContext ctx) = LoadTable(PlainAccdb, FormatDescriptor.Jet4Family, "T");
        ColumnDescriptor colW = table.Columns.First(c => c.Name == "W");
        ColumnDescriptor colV = table.Columns.First(c => c.Name == "V");
        var binder = new ExpressionRuleBinder(ctx);

        string[] textForms =
        [
            "<> 'a'", "<> 'a' And <> 'b'", "<> 'a' And <> 'b' And <> 'c'", "= 'a' Or = 'b'",
            "> 'a' And < 'z'", ">= 'a' And <= 'z'", "(<> 'a') And <> 'b'", "(<> 'a' And <> 'b')",
            "((<> 'a') And (<> 'b'))", "(= 'a' Or = 'b') And <> 'b'", "Not = 'a'", "Not = 'a' And <> 'b'",
            "Not (= 'a' Or = 'b')", "Not 'a'", "[W] <> 'a' And <> 'b'", "<> 'a' And [W] <> 'b'",
            "Like 'a*' Or Like 'b*'", "Like 'a*' And <> 'ab'", "Between 'a' And 'm'", "In ('a', 'b')",
            "Is Not Null", "Is Null", "= 'a' Xor Like 'a*'", "'a' Or 'b'",
            "Not Not = 'a'", "Not Is Null", "= 'a' Eqv = 'b'", "= 'a' Imp = 'b'",
            "'a' Or <> 'b'", "In ('a', 'b') Or = 'c'", "Between 'a' And 'm' And <> 'e'",
            "> 'a' And Between 'b' And 'm'", "(> 'a')", "<> 'a' And Not 'b'", "<> 'a' And Not = 'b'",
            "<> 'a' = False", "<> 'a' <> 'b'", "And <> 'b'",
        ];
        foreach (string ruleText in textForms)
        {
            using IMutationRule? rule = binder.BindFieldRule(table, colW, ruleText, out string? notAdmitted);
            Assert.True(rule is not null && notAdmitted is null, $"W rule \"{ruleText}\" failed to bind: {notAdmitted}");
        }

        string[] numForms =
        [
            "> 0 And < 10", ">= 0 And <= 10", "<> 0 And <> 5", "< 0 Or > 10",
            "(> 0 And < 10) Or = -5", "Not (< 0 Or > 10)", "Not = 0 And <> 5",
            "[V] > 0 And < 10", "> 0 And [V] < 10", "Between 1 And 10", "In (1, 3, 5)",
            "> 0 Eqv < 10", "> 0 Imp < 10", "1 Or 2", "1 Or > 5",
            "Between 1 And 10 And <> 5", "In (1, 3) Or = 5", "Not 0 And <> 5", "And <> 5",
        ];
        foreach (string ruleText in numForms)
        {
            using IMutationRule? rule = binder.BindFieldRule(table, colV, ruleText, out string? notAdmitted);
            Assert.True(rule is not null && notAdmitted is null, $"V rule \"{ruleText}\" failed to bind: {notAdmitted}");
        }

        // Mixed clause referencing a different column must be refused as NotModelled.FieldRuleForm.
        foreach ((ColumnDescriptor col, string ruleText) in new[]
        {
            (colW, "<> 'a' And [V] > 0"),
            (colW, "[V] > 0 And <> 'a'"),
            (colV, "> 0 And [W] <> 'a'"),
        })
        {
            using IMutationRule? rule = binder.BindFieldRule(table, col, ruleText, out string? notAdmitted);
            Assert.Null(rule);
            Assert.StartsWith("NotModelled.FieldRuleForm", notAdmitted, StringComparison.Ordinal);
        }

        // TableDef.ValidationRule does not expand implied operands (r-q42-field-rule-and/edge-forms-accdb).
        foreach (string tableImplied in new[] { "<> 'a' And <> 'b'", "<> 'a'" })
        {
            using IMutationRule? rule = binder.BindTableRule(table, tableImplied, out string? notAdmitted);
            Assert.Null(rule);
            Assert.Contains(ExpressionMessages.MissingOperator, notAdmitted, StringComparison.Ordinal);
        }

        // Existing Table R fixture binds single and multi-clause implied rules.
        (TableDefinition tableR, ExpressionContext ctxR) = LoadTable(Txn1bAccdb, FormatDescriptor.Jet4Family, "R");
        ColumnDescriptor colZ = tableR.Columns.First(c => c.Name == "Z");
        var binderR = new ExpressionRuleBinder(ctxR);
        using IMutationRule? multi = binderR.BindFieldRule(tableR, colZ, "<> 'a' And <> 'b'", out string? multiNotAdmitted);
        Assert.Null(multiNotAdmitted);
        Assert.NotNull(multi);
    }

    [Fact]
    public void BindFieldRule_replays_r_q42_disc_text_forms()
    {
        // r-q42-field-rule-and/disc-text-forms-accdb (R01-R20) and core rule-* text forms
        (TableDefinition table, ExpressionContext ctx) = LoadTable(PlainAccdb, FormatDescriptor.Jet4Family, "T");
        ColumnDescriptor colW = table.Columns.First(c => c.Name == "W");
        var binder = new ExpressionRuleBinder(ctx);

        (string Rule, (string? Value, RuleVerdict Expected)[] Cases)[] matrix =
        [
            ("<> 'a'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("<> 'a' And <> 'b'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("<> 'a' And <> 'b' And <> 'c'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Reject), ("z", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("= 'a' Or = 'b'", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Admit), ("c", RuleVerdict.Reject), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            ("> 'a' And < 'z'", [("a", RuleVerdict.Reject), ("g", RuleVerdict.Admit), ("z", RuleVerdict.Reject), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            (">= 'a' And <= 'z'", [("a", RuleVerdict.Admit), ("g", RuleVerdict.Admit), ("z", RuleVerdict.Admit), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            ("(<> 'a') And <> 'b'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("(<> 'a' And <> 'b')", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("((<> 'a') And (<> 'b'))", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("(= 'a' Or = 'b') And <> 'b'", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Reject)]),
            ("Not = 'a'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("Not = 'a' And <> 'b'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("Not (= 'a' Or = 'b')", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("Not 'a'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("[W] <> 'a' And <> 'b'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("<> 'a' And [W] <> 'b'", [("a", RuleVerdict.Reject), ("b", RuleVerdict.Reject), ("c", RuleVerdict.Admit)]),
            ("'a' Or 'b'", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Admit), ("c", RuleVerdict.Reject)]),
            ("Like 'a*' Or Like 'b*'", [("ax", RuleVerdict.Admit), ("bx", RuleVerdict.Admit), ("zx", RuleVerdict.Reject), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            ("Like 'a*' And <> 'ab'", [("ax", RuleVerdict.Admit), ("ab", RuleVerdict.Reject), ("bx", RuleVerdict.Reject)]),
            ("Between 'a' And 'm'", [("a", RuleVerdict.Admit), ("g", RuleVerdict.Admit), ("m", RuleVerdict.Admit), ("z", RuleVerdict.Reject), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            ("In ('a', 'b')", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Admit), ("c", RuleVerdict.Reject), ("", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            ("= 'a' Xor Like 'a*'", [("a", RuleVerdict.Reject), ("ax", RuleVerdict.Admit), ("zx", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
        ];

        foreach ((string ruleText, (string? Value, RuleVerdict Expected)[] cases) in matrix)
        {
            using IMutationRule? rule = binder.BindFieldRule(table, colW, ruleText, out string? notAdmitted);
            Assert.True(rule is not null && notAdmitted is null, $"Rule \"{ruleText}\": {notAdmitted}");
            foreach ((string? value, RuleVerdict expected) in cases)
            {
                RuleResult actual = rule!.Evaluate(new SingleValueCursor(table, colW, TextValue(value)));
                Assert.True(actual.Verdict == expected, $"Rule \"{ruleText}\" with W={(value is null ? "Null" : $"'{value}'")}: expected {expected}, got {actual.Verdict} ({actual.Detail})");
            }
        }

        // R19 (<> 'a' <> 'b') and R20 (And <> 'b') compare computed VT_I2 Boolean (-1/0 -> "-1"/"0") against Text 'b':
        // both admit every tested value in r-q42-field-rule-and/disc-text-forms-accdb (calls 254-259) and edge-forms-{accdb,mdb} (calls 112-116).
        (string Rule, (string? Value, RuleVerdict Expected)[] Cases)[] boolTextMatrix =
        [
            ("<> 'a' <> 'b'", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Admit), ("z", RuleVerdict.Admit), ("0", RuleVerdict.Admit), ("-1", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("And <> 'b'", [("a", RuleVerdict.Admit), ("b", RuleVerdict.Admit), ("z", RuleVerdict.Admit), ("And", RuleVerdict.Admit), ("", RuleVerdict.Admit), (null, RuleVerdict.Admit)]),
            ("<> 'a' = False", [("a", RuleVerdict.Admit), ("z", RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
        ];
        foreach ((string ruleText, (string? Value, RuleVerdict Expected)[] cases) in boolTextMatrix)
        {
            using IMutationRule? rule = binder.BindFieldRule(table, colW, ruleText, out string? notAdmitted);
            Assert.True(rule is not null && notAdmitted is null, $"Rule \"{ruleText}\": {notAdmitted}");
            foreach ((string? value, RuleVerdict expected) in cases)
            {
                RuleResult actual = rule!.Evaluate(new SingleValueCursor(table, colW, TextValue(value)));
                Assert.True(actual.Verdict == expected, $"Rule \"{ruleText}\" with W={(value is null ? "Null" : $"'{value}'")}: expected {expected}, got {actual.Verdict} ({actual.Detail})");
            }
        }
    }

    [Fact]
    public void BindFieldRule_replays_r_q42_disc_num_forms()
    {
        // r-q42-field-rule-and/disc-num-forms-accdb (N01-N12) and r-q-expr-sql/d-validation-andor
        (TableDefinition table, ExpressionContext ctx) = LoadTable(PlainAccdb, FormatDescriptor.Jet4Family, "T");
        ColumnDescriptor colV = table.Columns.First(c => c.Name == "V");
        var binder = new ExpressionRuleBinder(ctx);

        (string Rule, (int? Value, RuleVerdict Expected)[] Cases)[] matrix =
        [
            ("> 0 And < 10", [(0, RuleVerdict.Reject), (5, RuleVerdict.Admit), (10, RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
            (">= 0 And <= 10", [(-1, RuleVerdict.Reject), (0, RuleVerdict.Admit), (5, RuleVerdict.Admit), (10, RuleVerdict.Admit), (11, RuleVerdict.Reject)]),
            ("<> 0 And <> 5", [(0, RuleVerdict.Reject), (5, RuleVerdict.Reject), (3, RuleVerdict.Admit)]),
            ("< 0 Or > 10", [(-1, RuleVerdict.Admit), (5, RuleVerdict.Reject), (11, RuleVerdict.Admit)]),
            ("(> 0 And < 10) Or = -5", [(-5, RuleVerdict.Admit), (-1, RuleVerdict.Reject), (5, RuleVerdict.Admit), (11, RuleVerdict.Reject)]),
            ("Not (< 0 Or > 10)", [(-1, RuleVerdict.Reject), (0, RuleVerdict.Admit), (5, RuleVerdict.Admit), (10, RuleVerdict.Admit), (11, RuleVerdict.Reject)]),
            ("Not = 0 And <> 5", [(0, RuleVerdict.Reject), (5, RuleVerdict.Reject), (3, RuleVerdict.Admit)]),
            ("[V] > 0 And < 10", [(0, RuleVerdict.Reject), (5, RuleVerdict.Admit), (10, RuleVerdict.Reject)]),
            ("> 0 And [V] < 10", [(0, RuleVerdict.Reject), (5, RuleVerdict.Admit), (10, RuleVerdict.Reject)]),
            ("Between 1 And 10 And <> 5", [(0, RuleVerdict.Reject), (1, RuleVerdict.Admit), (5, RuleVerdict.Reject), (10, RuleVerdict.Admit), (11, RuleVerdict.Reject)]),
            ("In (1, 3) Or = 5", [(1, RuleVerdict.Admit), (2, RuleVerdict.Reject), (3, RuleVerdict.Admit), (5, RuleVerdict.Admit)]),
            ("1 Or 2", [(1, RuleVerdict.Admit), (2, RuleVerdict.Admit), (3, RuleVerdict.Reject)]),
            ("([V] > 0) And Null", [(5, RuleVerdict.Admit), (-1, RuleVerdict.Reject), (null, RuleVerdict.Admit)]),
        ];

        foreach ((string ruleText, (int? Value, RuleVerdict Expected)[] cases) in matrix)
        {
            using IMutationRule? rule = binder.BindFieldRule(table, colV, ruleText, out string? notAdmitted);
            Assert.True(rule is not null && notAdmitted is null, $"Rule \"{ruleText}\": {notAdmitted}");
            foreach ((int? value, RuleVerdict expected) in cases)
            {
                RuleResult actual = rule!.Evaluate(new SingleValueCursor(table, colV, LongValue(value)));
                Assert.True(actual.Verdict == expected, $"Rule \"{ruleText}\" with V={(value is null ? "Null" : value.ToString())}: expected {expected}, got {actual.Verdict} ({actual.Detail})");
            }
        }
    }

    [Theory]
    [InlineData(PlainAccdb)]
    [InlineData(PlainMdb)]
    public void BindFieldRule_replays_r_q42_edge_forms_on_accdb_and_mdb(string fixtureSha)
    {
        // r-q42-field-rule-and/edge-forms-accdb, r-q42-field-rule-and/edge-forms-mdb
        (TableDefinition table, ExpressionContext ctx) = LoadTable(fixtureSha, FormatDescriptor.Jet4Family, "T");
        ColumnDescriptor colW = table.Columns.First(c => c.Name == "W");
        ColumnDescriptor colV = table.Columns.First(c => c.Name == "V");
        var binder = new ExpressionRuleBinder(ctx);

        // Case-insensitivity under General collation
        AssertTextRule(binder, table, colW, "<> 'a' And <> 'b'",
            ("A", RuleVerdict.Reject), ("B", RuleVerdict.Reject), ("Z", RuleVerdict.Admit));

        // E1-E3: Is Not Null, Is Null, Not Is Null
        AssertTextRule(binder, table, colW, "Is Not Null",
            (null, RuleVerdict.Reject), ("", RuleVerdict.Admit), ("x", RuleVerdict.Admit));
        AssertTextRule(binder, table, colW, "Is Null",
            (null, RuleVerdict.Admit), ("", RuleVerdict.Reject), ("x", RuleVerdict.Reject));
        AssertTextRule(binder, table, colW, "Not Is Null",
            (null, RuleVerdict.Reject), ("", RuleVerdict.Admit), ("x", RuleVerdict.Admit));

        // E4: <> 'a' = False -> ([W] <> 'a') = False
        AssertTextRule(binder, table, colW, "<> 'a' = False",
            ("a", RuleVerdict.Admit), ("z", RuleVerdict.Reject), (null, RuleVerdict.Admit));

        // E7: And <> 5 on Long fails every non-null write with 3802 (RuleVerdict.Failed)
        using (IMutationRule? e7 = binder.BindFieldRule(table, colV, "And <> 5", out string? e7Err))
        {
            Assert.True(e7 is not null && e7Err is null, e7Err);
            foreach (int v in new[] { 0, 1, 5, -1 })
            {
                Assert.Equal(RuleVerdict.Failed, e7!.Evaluate(new SingleValueCursor(table, colV, LongValue(v))).Verdict);
            }
        }

        // E8: > 0 Eqv < 10; E9: > 0 Imp < 10
        AssertLongRule(binder, table, colV, "> 0 Eqv < 10",
            (5, RuleVerdict.Admit), (0, RuleVerdict.Reject), (15, RuleVerdict.Reject), (null, RuleVerdict.Admit));
        AssertLongRule(binder, table, colV, "> 0 Imp < 10",
            (5, RuleVerdict.Admit), (0, RuleVerdict.Admit), (15, RuleVerdict.Reject), (null, RuleVerdict.Admit));
    }

    private static void AssertTextRule(ExpressionRuleBinder binder, TableDefinition table, ColumnDescriptor col, string ruleText, params (string? Value, RuleVerdict Expected)[] cases)
    {
        using IMutationRule? rule = binder.BindFieldRule(table, col, ruleText, out string? notAdmitted);
        Assert.True(rule is not null && notAdmitted is null, $"Rule \"{ruleText}\": {notAdmitted}");
        foreach ((string? value, RuleVerdict expected) in cases)
        {
            RuleResult actual = rule!.Evaluate(new SingleValueCursor(table, col, TextValue(value)));
            Assert.True(actual.Verdict == expected, $"Rule \"{ruleText}\" with W={(value is null ? "Null" : $"'{value}'")}: expected {expected}, got {actual.Verdict} ({actual.Detail})");
        }
    }

    private static void AssertLongRule(ExpressionRuleBinder binder, TableDefinition table, ColumnDescriptor col, string ruleText, params (int? Value, RuleVerdict Expected)[] cases)
    {
        using IMutationRule? rule = binder.BindFieldRule(table, col, ruleText, out string? notAdmitted);
        Assert.True(rule is not null && notAdmitted is null, $"Rule \"{ruleText}\": {notAdmitted}");
        foreach ((int? value, RuleVerdict expected) in cases)
        {
            RuleResult actual = rule!.Evaluate(new SingleValueCursor(table, col, LongValue(value)));
            Assert.True(actual.Verdict == expected, $"Rule \"{ruleText}\" with V={(value is null ? "Null" : value.ToString())}: expected {expected}, got {actual.Verdict} ({actual.Detail})");
        }
    }

    private static NativeValue TextValue(string? text)
        => text is null ? new NativeValue(NativeKind.Null) : new NativeValue(NativeKind.Text) { Text = text };

    private static NativeValue LongValue(int? value)
        => value is null ? new NativeValue(NativeKind.Null) : new NativeValue(NativeKind.Long) { Integer = value.Value, Bits = (uint)value.Value };

    private static (TableDefinition Table, ExpressionContext Context) LoadTable(string fixtureSha, FormatDescriptor d, string tableName)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CalcDb.slnx")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        byte[] file = ArtifactStore.ForRepository(root).ReadAll(fixtureSha);
        var image = new ImagePageSource(file, d.PageSize);
        var cache = new PageCache(image, 1, d.PageSize, 256);
        CatalogSnapshot catalog = CatalogBootstrap.Open(cache, d, image.PageCount, out _)!;
        CatalogEntry entry = catalog.LocalTables(d).First(e => string.Equals(e.Name, tableName, StringComparison.OrdinalIgnoreCase));
        TableDefinition table = TableDefDecoder.Decode(
            TableDefSegmentView.Open((uint)entry.Id, p =>
            {
                using PagePin pin = cache.Pin(p).Pin!;
                return pin.Bytes.ToArray();
            }, d, 8, out _)!,
            (uint)entry.Id,
            d,
            out _)!;
        DatabaseHeader header = DatabaseHeader.Decode(file.AsSpan(0, d.PageSize), d);
        var ctx = new ExpressionContext(7177, TextCompareMode.Database, DatabaseCollation.For(header.SortOrder), ExpressionUse.Query);
        return (table, ctx);
    }

    private sealed class SingleValueCursor(TableDefinition table, ColumnDescriptor column, NativeValue value) : IRecordCursor
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
        public byte[]? GetBookmark(out CursorFailure? failure) { failure = CursorFailure.NotObserved; return null; }
        public CursorFailure? SetBookmark(ReadOnlySpan<byte> bookmark) => CursorFailure.NotObserved;
        public FieldResult Read(ColumnDescriptor c) => c.Ordinal == column.Ordinal ? FieldResult.Of(value) : FieldResult.Of(new NativeValue(NativeKind.Null));
        public TextFieldRead ReadText(ColumnDescriptor c)
        {
            if (c.Ordinal == column.Ordinal && value.Kind == NativeKind.Text)
            {
                string text = value.Text ?? "";
                return new TextFieldRead(MemoryMarshal.AsBytes(text.AsSpan()), new TextShape(text.Length, Compressed: false, Failure: null));
            }
            return new TextFieldRead(Read(c));
        }
        public void Dispose() { }
    }
}
