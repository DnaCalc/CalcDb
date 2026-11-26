using System.Text;

using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>The kinds of syntax node of the Access expression grammar (II-10 §2).</summary>
public enum SyntaxKind : byte
{
    /// <summary>A number, text, date, True/False or Null literal: <see cref="SyntaxNode.Value"/>.</summary>
    Literal,

    /// <summary>A name, as written (bracketed names without their brackets; a qualified name with its dots).</summary>
    Name,

    /// <summary>A unary operator (<see cref="ScalarOperation.Negate"/>, <see cref="ScalarOperation.Not"/>).</summary>
    Unary,

    /// <summary>A binary operator (arithmetic, <c>&amp;</c>, comparison, logical).</summary>
    Binary,

    /// <summary>A function call: <see cref="SyntaxNode.Text"/> and <see cref="SyntaxNode.Items"/>.</summary>
    Call,

    /// <summary><c>x [Not] Like p</c>.</summary>
    Like,

    /// <summary><c>x Is [Not] Null</c>.</summary>
    IsNull,

    /// <summary><c>x [Not] In (a, b, …)</c>.</summary>
    In,

    /// <summary><c>x [Not] Between a And b</c>.</summary>
    Between,

    /// <summary><c>*</c> as the whole argument list of a call (SQL <c>Count(*)</c>; W-06/parser-binder-scan).</summary>
    Star,

    /// <summary>A scalar subquery <c>(SELECT …)</c> in an expression (R-SQL-1 SQL-SUB-03/04).</summary>
    Subquery,

    /// <summary><c>EXISTS (SELECT …)</c> (R-SQL-1 SQL-SUB-03/04).</summary>
    Exists,

    /// <summary><c>x [NOT] IN (SELECT …)</c> (R-SQL-1 SQL-SUB-03/04).</summary>
    InSubquery,

    /// <summary><c>x &lt;cmp&gt; ANY|SOME|ALL (SELECT …)</c> (R-SQL-1 SQL-SUB-03/04).</summary>
    QuantifiedComparison,
}

/// <summary>
/// Callback supplied by <c>CalcDb.Sql</c> to parse a nested SQL <c>SELECT</c> inside an expression (subqueries
/// in <c>WHERE</c>, <c>SELECT</c>, <c>IN</c>, <c>EXISTS</c>, <c>ANY</c>/<c>ALL</c>). When null (Find criteria,
/// validation rules, action queries), subquery forms keep refusing with <c>NotModelled.Subquery</c>.
/// </summary>
public delegate bool SubqueryParser(string source, int start, out object? subquery, out int end, out ExpressionFailure? failure);

/// <summary>What a literal is, which decides its operand contract and its static type.</summary>
public enum LiteralKind : byte
{
    Number,
    Text,
    Date,
    Boolean,
    Null,
}

/// <summary>
/// One node of a parsed expression (II-10 §2: the parse keeps the source; binding is separate). A literal keeps its value
/// (an owned BSTR for text, released by <see cref="ExpressionSyntax.Dispose"/>) and whether its spelling doubled its
/// delimiter (<c>'O''Brien'</c>), which an indexed Find answers differently (CUR-FIND indexed Text).
/// </summary>
public sealed class SyntaxNode
{
    public required SyntaxKind Kind { get; init; }

    public ScalarOperation Operation { get; init; }

    public LiteralKind Literal { get; init; }

    public NativeVariant Value { get; init; }

    public string? Text { get; init; }

    public bool Negated { get; init; }

    public bool DoubledDelimiter { get; init; }

    public SyntaxNode? Left { get; init; }

    public SyntaxNode? Right { get; init; }

    public IReadOnlyList<SyntaxNode> Items { get; init; } = [];

    /// <summary>The parsed SQL subquery AST for <see cref="SyntaxKind.Subquery"/>, <see cref="SyntaxKind.Exists"/>, <see cref="SyntaxKind.InSubquery"/>, or <see cref="SyntaxKind.QuantifiedComparison"/>.</summary>
    public object? Subquery { get; init; }

    /// <summary>True for <c>ALL</c> and false for <c>ANY</c>/<c>SOME</c> on <see cref="SyntaxKind.QuantifiedComparison"/>.</summary>
    public bool QuantifierAll { get; init; }

    /// <summary>The offset of the node's first unit in the source.</summary>
    public int Offset { get; init; }

    /// <summary>For parenthesized calls and subqueries, the offset of the closing <c>)</c> in the source (0 if unset).</summary>
    public int CloseOffset { get; init; }
}

/// <summary>A parsed expression: its root, or the syntax error ACE reports for the text (3077).</summary>
public sealed unsafe class ExpressionSyntax : IDisposable
{
    private readonly List<nint> _texts;
    private readonly List<IDisposable> _subqueries;

    internal ExpressionSyntax(SyntaxNode? root, ExpressionFailure? failure, List<nint> texts, List<IDisposable>? subqueries = null)
    {
        Root = root;
        Failure = failure;
        _texts = texts;
        _subqueries = subqueries ?? [];
    }

    public SyntaxNode? Root { get; }

    public ExpressionFailure? Failure { get; }

    /// <summary>
    /// Parse <paramref name="source"/> with the Access grammar (II-10 §2): an operator-precedence parser over a lexer that
    /// reads one token at a time, so the first error in reading order is the one reported (<c>S = 'O'Brien'</c> stops at
    /// <c>Brien</c>, a missing operator, before its unterminated string). Precedence, lowest first: Imp, Eqv, Xor, Or,
    /// And, Not, comparison (= &lt;&gt; &lt; &gt; &lt;= &gt;= Like Is In Between), &amp;, + -, Mod, \, * /, unary -, ^.
    /// </summary>
    public static ExpressionSyntax Parse(ReadOnlySpan<char> source) => Parse(source, null);

    /// <summary>Parse <paramref name="source"/>, optionally parsing embedded SQL subqueries via <paramref name="parseSubquery"/>.</summary>
    public static ExpressionSyntax Parse(ReadOnlySpan<char> source, SubqueryParser? parseSubquery)
    {
        var parser = new Parser(source.ToString(), 0, parseSubquery);
        try
        {
            SyntaxNode root = parser.Expression();
            parser.ExpectEnd();
            return new ExpressionSyntax(root, null, parser.Texts, parser.Subqueries);
        }
        catch (SyntaxError e)
        {
            return parser.Failed(e);
        }
        catch (NotModelledSyntax e)
        {
            return parser.Failed(e);
        }
        catch (SubqueryError e)
        {
            return parser.Failed(e);
        }
    }

    /// <summary>
    /// Parse one expression of a larger text (W-06/parser-binder-scan, II-11 §2: the SQL grammar's expressions are this
    /// grammar), from <paramref name="start"/> up to the first token that cannot continue it: a SQL word such as
    /// <c>FROM</c> or <c>AS</c>, a comma, <c>;</c>, a closing parenthesis it did not open, or the end. <paramref name="end"/>
    /// is that token's offset. On a syntax error the failure is the Find context's 3077 with <see cref="ExpressionFailure.Site"/>
    /// at the offset where it was detected; the SQL layer reports it in its own words (3075).
    /// </summary>
    public static ExpressionSyntax ParseFragment(string source, int start, out int end) =>
        ParseFragment(source, start, null, out end);

    /// <summary>Parse one expression fragment of a larger SQL statement, optionally parsing embedded SQL subqueries.</summary>
    public static ExpressionSyntax ParseFragment(string source, int start, SubqueryParser? parseSubquery, out int end)
    {
        var parser = new Parser(source, start, parseSubquery);
        try
        {
            SyntaxNode root = parser.Expression();
            end = parser.Position;
            return new ExpressionSyntax(root, null, parser.Texts, parser.Subqueries);
        }
        catch (SyntaxError e)
        {
            end = parser.ErrorPosition;
            return parser.Failed(e);
        }
        catch (NotModelledSyntax e)
        {
            end = parser.ErrorPosition;
            return parser.Failed(e);
        }
        catch (SubqueryError e)
        {
            end = parser.ErrorPosition;
            return parser.Failed(e);
        }
    }

    /// <summary>
    /// Expands implied left operands and bare literals inside a field-level <c>ValidationRule</c>
    /// (<c>evidence/r-q42-field-rule-and/</c>, qualified.md <c>R-EXPR-1 EXPR-MUTATION r-q42</c>), using the same lexer
    /// and operator-precedence grammar as <see cref="Parse"/> without altering regular SQL or TableDef parsing.
    /// </summary>
    internal static string ExpandFieldRule(string rule, string field, out FieldRuleForm form)
    {
        var parser = new Parser(rule, 0);
        var edits = new List<SourceEdit>();
        var state = new FieldRuleState { AllClausesImpliedNotEqual = true };
        try
        {
            if (parser.TryLeadingAndNotEqual(field, edits, ref state))
            {
                form = FieldRuleForm.MultiClause;
                return ApplyEdits(rule, edits);
            }
            SyntaxNode root = parser.FieldExpression(field, edits, ref state, out _);
            parser.ExpectEnd();
            if (state.HasUnrecordedForm)
            {
                form = FieldRuleForm.UnrecordedMultiClause;
                return rule;
            }
            if (edits.Count == 0)
            {
                if (!state.HasLogical && !state.HasParen && !state.HasNotPrefix && root.Kind == SyntaxKind.Literal)
                {
                    form = FieldRuleForm.Literal;
                    return $"{field} = ({rule})";
                }
                form = FieldRuleForm.Direct;
                return rule;
            }
            if (!state.HasLogical && !state.HasParen && !state.HasNotPrefix)
            {
                form = state.AnyLiteralRewrite ? FieldRuleForm.Literal : FieldRuleForm.SingleImplied;
            }
            else if (state.HasLogical && !state.HasOtherLogical && !state.HasParen && !state.HasNotPrefix
                && state.AllClausesImpliedNotEqual && state.ClauseCount >= 2)
            {
                form = FieldRuleForm.AndNotEqualImplied;
            }
            else
            {
                form = FieldRuleForm.MultiClause;
            }
            return ApplyEdits(rule, edits);
        }
        catch (SyntaxError)
        {
            form = FieldRuleForm.Unparsed;
            return ApplyEdits(rule, edits);
        }
        catch (NotModelledSyntax)
        {
            form = FieldRuleForm.Unparsed;
            return ApplyEdits(rule, edits);
        }
        finally
        {
            parser.FreeTexts();
        }
    }

    private readonly record struct SourceEdit(int Start, int Length, string Replacement);

    private struct FieldRuleState
    {
        public bool HasParen;
        public bool HasLogical;
        public bool HasOtherLogical;
        public bool HasNotPrefix;
        public bool AnyLiteralRewrite;
        public bool AllClausesImpliedNotEqual;
        public bool HasUnrecordedForm;
        public int ClauseCount;
    }

    private static string ApplyEdits(string rule, List<SourceEdit> edits)
    {
        if (edits.Count == 0)
        {
            return rule;
        }
        var sb = new StringBuilder(rule.Length + edits.Count * 16);
        int pos = 0;
        foreach (SourceEdit edit in edits)
        {
            if (edit.Start > pos)
            {
                sb.Append(rule, pos, edit.Start - pos);
            }
            sb.Append(edit.Replacement);
            pos = edit.Start + edit.Length;
        }
        if (pos < rule.Length)
        {
            sb.Append(rule, pos, rule.Length - pos);
        }
        return sb.ToString();
    }

    /// <summary>Free the text literals' BSTRs (a bound expression that keeps them takes them over: <see cref="TakeTexts"/>) and any parsed subqueries.</summary>
    public void Dispose()
    {
        foreach (nint b in _texts)
        {
            Bstrs.Free(b);
        }
        _texts.Clear();
        foreach (IDisposable sub in _subqueries)
        {
            sub.Dispose();
        }
        _subqueries.Clear();
    }

    /// <summary>Hand the text literals' BSTRs to a new owner.</summary>
    internal nint[] TakeTexts()
    {
        nint[] taken = [.. _texts];
        _texts.Clear();
        return taken;
    }

    internal sealed class SyntaxError(string message) : Exception(message);

    /// <summary>A form of the grammar CalcDb does not parse yet (a subquery, ALIKE, a parameter marker): named, never guessed.</summary>
    internal sealed class NotModelledSyntax(string name, string detail) : Exception(detail)
    {
        public string Name { get; } = name;
    }

    private sealed class SubqueryError(ExpressionFailure failure) : Exception(failure.Message)
    {
        public ExpressionFailure Failure { get; } = failure;
    }

    private enum TokenKind : byte
    {
        End,
        Number,
        Text,
        Date,
        Name,
        Symbol,
        Keyword,
    }

    private readonly record struct Token(TokenKind Kind, int Start, int Length, string Text, bool Doubled = false);

    /// <summary>The recursive-descent form of the precedence table; nesting depth is bounded by <see cref="MaxDepth"/>.</summary>
    private sealed class Parser(string source, int start, SubqueryParser? parseSubquery = null)
    {
        private const int MaxDepth = 200;

        private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "And", "Or", "Not", "Xor", "Eqv", "Imp", "Mod", "Like", "Is", "Null", "In", "Between", "True", "False",
        };

        private int _position = start;
        private int _lastTokenEnd = start;
        private Token? _peeked;
        private int _depth;

        public List<nint> Texts { get; } = [];

        public List<IDisposable> Subqueries { get; } = [];

        /// <summary>The offset of the first token not consumed.</summary>
        public int Position => _peeked?.Start ?? SkipSpace(_position);

        /// <summary>Where the lexer stood when an error was raised: the end of the token it had read last.</summary>
        public int ErrorPosition => _peeked is { } t ? t.Start + t.Length : _position;

        private int SkipSpace(int p)
        {
            while (p < source.Length && char.IsWhiteSpace(source[p]))
            {
                p++;
            }
            return p;
        }

        public void FreeTexts()
        {
            foreach (nint b in Texts)
            {
                Bstrs.Free(b);
            }
            Texts.Clear();
            foreach (IDisposable sub in Subqueries)
            {
                sub.Dispose();
            }
            Subqueries.Clear();
        }

        public ExpressionSyntax Failed(SyntaxError e)
        {
            FreeTexts();
            return new ExpressionSyntax(null, ExpressionFailure.Ace(ExpressionPhase.Parse, 3077, e.Message) with { Site = ErrorPosition }, []);
        }

        public ExpressionSyntax Failed(NotModelledSyntax e)
        {
            FreeTexts();
            return new ExpressionSyntax(null, ExpressionFailure.Open(ExpressionPhase.Parse, e.Name, e.Message) with { Site = ErrorPosition }, []);
        }

        public ExpressionSyntax Failed(SubqueryError e)
        {
            FreeTexts();
            return new ExpressionSyntax(null, e.Failure, []);
        }

        private object ParseSubqueryAt(int startOffset, string keyword)
        {
            if (parseSubquery is null)
            {
                throw new NotModelledSyntax("NotModelled.Subquery", $"a subquery ({keyword}): R-SQL-1 SQL-SUB, W-06/relational-spill");
            }
            if (!parseSubquery(source, startOffset, out object? sub, out int end, out ExpressionFailure? fail))
            {
                throw new SubqueryError(fail ?? ExpressionFailure.Open(ExpressionPhase.Parse, "NotModelled.Subquery", "subquery parse failed"));
            }
            if (sub is IDisposable disposable)
            {
                Subqueries.Add(disposable);
            }
            _position = end;
            _lastTokenEnd = end;
            _peeked = null;
            return sub!;
        }

        private Token Peek() => _peeked ??= Lex();

        private Token Next()
        {
            Token t = Peek();
            _peeked = null;
            _lastTokenEnd = t.Start + t.Length;
            return t;
        }

        private bool IsKeyword(string word) => Peek() is { Kind: TokenKind.Keyword } t && string.Equals(t.Text, word, StringComparison.OrdinalIgnoreCase);

        private bool IsSymbol(string symbol) => Peek() is { Kind: TokenKind.Symbol } t && t.Text == symbol;

        public void ExpectEnd()
        {
            Token t = Peek();
            if (t.Kind != TokenKind.End)
            {
                throw new SyntaxError(t.Kind == TokenKind.Symbol && t.Text == ";" ? ExpressionMessages.SyntaxError : ExpressionMessages.MissingOperator);
            }
        }

        public SyntaxNode Expression()
        {
            if (++_depth > MaxDepth)
            {
                throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
            try
            {
                return Imp();
            }
            finally
            {
                _depth--;
            }
        }

        public bool TryLeadingAndNotEqual(string field, List<SourceEdit> edits, ref FieldRuleState state)
        {
            if (!IsKeyword("And"))
            {
                return false;
            }
            // r-q42-field-rule-and/put-refusals-accdb call 16, disc-text-forms-accdb R20, edge-forms-accdb E6/E7:
            // sole top-level "And <> <rhs>" parses the leading And as the bare text literal 'And' (so "And <> 5" on Long fails 3802).
            Token andToken = Next();
            if (Peek() is not { Kind: TokenKind.Symbol, Text: "<>" })
            {
                throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
            state.AllClausesImpliedNotEqual = false;
            state.ClauseCount++;
            edits.Add(new SourceEdit(andToken.Start, andToken.Length, $"({field} = 'And')"));
            var synthetic = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = ScalarOperation.Equal, Offset = andToken.Start };
            ComparisonSuffix(synthetic);
            if (Peek().Kind != TokenKind.End)
            {
                edits.Clear();
                throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
            return true;
        }

        public SyntaxNode FieldExpression(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral)
        {
            Guard();
            try
            {
                return FieldImp(field, edits, ref state, out isBareLiteral);
            }
            finally
            {
                _depth--;
            }
        }

        private SyntaxNode FieldImp(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral) =>
            FieldLogical(field, edits, ref state, "Imp", ScalarOperation.Imp, isAnd: false, FieldEqv, out isBareLiteral);

        private SyntaxNode FieldEqv(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral) =>
            FieldLogical(field, edits, ref state, "Eqv", ScalarOperation.Eqv, isAnd: false, FieldXor, out isBareLiteral);

        private SyntaxNode FieldXor(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral) =>
            FieldLogical(field, edits, ref state, "Xor", ScalarOperation.Xor, isAnd: false, FieldOr, out isBareLiteral);

        private SyntaxNode FieldOr(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral) =>
            FieldLogical(field, edits, ref state, "Or", ScalarOperation.Or, isAnd: false, FieldAnd, out isBareLiteral);

        private SyntaxNode FieldAnd(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral) =>
            FieldLogical(field, edits, ref state, "And", ScalarOperation.And, isAnd: true, FieldNot, out isBareLiteral);

        private delegate SyntaxNode FieldLevel(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral);

        private SyntaxNode FieldLogical(
            string field,
            List<SourceEdit> edits,
            ref FieldRuleState state,
            string word,
            ScalarOperation operation,
            bool isAnd,
            FieldLevel next,
            out bool isBareLiteral)
        {
            SyntaxNode left = next(field, edits, ref state, out isBareLiteral);
            while (IsKeyword(word))
            {
                Token t = Next();
                state.HasLogical = true;
                if (!isAnd)
                {
                    state.HasOtherLogical = true;
                    state.AllClausesImpliedNotEqual = false;
                }
                isBareLiteral = false;
                SyntaxNode right = next(field, edits, ref state, out _);
                left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = operation, Left = left, Right = right, Offset = t.Start };
            }
            return left;
        }

        private SyntaxNode FieldNot(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral)
        {
            if (IsKeyword("Not"))
            {
                Token notToken = Next();
                isBareLiteral = false;
                state.AllClausesImpliedNotEqual = false;
                if (IsKeyword("Like") || IsKeyword("In") || IsKeyword("Between"))
                {
                    // Implied negated predicate: Not Like / Not In / Not Between (r-q42-field-rule-and/put-forms-accdb, disc-text-forms-accdb R06..R08).
                    state.ClauseCount++;
                    edits.Add(new SourceEdit(notToken.Start, 0, field + " "));
                    var syntheticField = new SyntaxNode { Kind = SyntaxKind.Name, Text = field, Offset = notToken.Start };
                    SyntaxNode predicate = ParsePredicateAfterNot(syntheticField, notToken.Start);
                    return ComparisonSuffix(predicate);
                }
                state.HasNotPrefix = true;
                Guard();
                try
                {
                    SyntaxNode operand = FieldNot(field, edits, ref state, out _);
                    return new SyntaxNode { Kind = SyntaxKind.Unary, Operation = ScalarOperation.Not, Left = operand, Offset = notToken.Start };
                }
                finally
                {
                    _depth--;
                }
            }
            return FieldClause(field, edits, ref state, out isBareLiteral);
        }

        private SyntaxNode FieldClause(string field, List<SourceEdit> edits, ref FieldRuleState state, out bool isBareLiteral)
        {
            isBareLiteral = false;
            if (IsSymbol("("))
            {
                int savedPosition = _position;
                int savedLastTokenEnd = _lastTokenEnd;
                Token? savedPeeked = _peeked;
                int savedEditCount = edits.Count;
                int savedTextCount = Texts.Count;
                FieldRuleState savedState = state;

                Next(); // (
                SyntaxNode inner = FieldExpression(field, edits, ref state, out bool innerBareLiteral);
                CloseParenthesis();

                if (IsTighterThanComparison() || (innerBareLiteral && IsComparisonStart()))
                {
                    _position = savedPosition;
                    _lastTokenEnd = savedLastTokenEnd;
                    _peeked = savedPeeked;
                    edits.RemoveRange(savedEditCount, edits.Count - savedEditCount);
                    for (int i = savedTextCount; i < Texts.Count; i++)
                    {
                        Bstrs.Free(Texts[i]);
                    }
                    Texts.RemoveRange(savedTextCount, Texts.Count - savedTextCount);
                    state = savedState;
                }
                else
                {
                    state.HasParen = true;
                    state.AllClausesImpliedNotEqual = false;
                    if (IsComparisonStart())
                    {
                        return ComparisonSuffix(inner);
                    }
                    isBareLiteral = innerBareLiteral;
                    return inner;
                }
            }

            if (IsImpliedComparisonStart())
            {
                Token first = Peek();
                state.ClauseCount++;
                edits.Add(new SourceEdit(first.Start, 0, field + " "));
                var syntheticField = new SyntaxNode { Kind = SyntaxKind.Name, Text = field, Offset = first.Start };
                SyntaxNode result = ComparisonSuffix(syntheticField);
                if (result is not { Kind: SyntaxKind.Binary, Operation: ScalarOperation.NotEqual } || !ReferenceEquals(result.Left, syntheticField))
                {
                    state.AllClausesImpliedNotEqual = false;
                }
                return result;
            }

            int clauseStart = Peek().Start;
            SyntaxNode expr = Comparison();
            state.ClauseCount++;
            state.AllClausesImpliedNotEqual = false;
            if (expr.Kind == SyntaxKind.Literal && expr.Literal is LiteralKind.Number or LiteralKind.Text or LiteralKind.Date)
            {
                int len = _lastTokenEnd - clauseStart;
                state.AnyLiteralRewrite = true;
                isBareLiteral = true;
                edits.Add(new SourceEdit(clauseStart, len, $"{field} = ({source.AsSpan(clauseStart, len)})"));
            }
            else if (IsNegatedNumericLiteral(expr))
            {
                // No ACE record decides bare negative numeric literals (-5, 1 Or -5, Not -5); refuse by name rather than
                // evaluating -5 as truthy inside a logical clause.
                isBareLiteral = true;
                state.HasUnrecordedForm = true;
            }
            return expr;
        }

        private static bool IsNegatedNumericLiteral(SyntaxNode node) =>
            node is { Kind: SyntaxKind.Unary, Operation: ScalarOperation.Negate, Left: { } inner }
            && (inner is { Kind: SyntaxKind.Literal, Literal: LiteralKind.Number } || IsNegatedNumericLiteral(inner));

        private bool IsTighterThanComparison() =>
            IsSymbol("&") || IsSymbol("+") || IsSymbol("-") || IsKeyword("Mod")
            || IsSymbol("\\") || IsSymbol("*") || IsSymbol("/") || IsSymbol("^");

        private bool IsImpliedComparisonStart()
        {
            Token t = Peek();
            if (t.Kind == TokenKind.Symbol && t.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                return true;
            }
            if (t.Kind == TokenKind.Name && t.Text.Equals("ALike", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return IsKeyword("Is") || IsKeyword("Like") || IsKeyword("In") || IsKeyword("Between");
        }

        private bool IsComparisonStart() => IsImpliedComparisonStart() || IsKeyword("Not");

        private SyntaxNode Imp() => LeftAssociative(Eqv, ("Imp", ScalarOperation.Imp));

        private SyntaxNode Eqv() => LeftAssociative(Xor, ("Eqv", ScalarOperation.Eqv));

        private SyntaxNode Xor() => LeftAssociative(Or, ("Xor", ScalarOperation.Xor));

        private SyntaxNode Or() => LeftAssociative(And, ("Or", ScalarOperation.Or));

        private SyntaxNode And() => LeftAssociative(Not, ("And", ScalarOperation.And));

        private SyntaxNode LeftAssociative(Func<SyntaxNode> operand, (string Word, ScalarOperation Operation) op)
        {
            SyntaxNode left = operand();
            while (IsKeyword(op.Word))
            {
                Token t = Next();
                SyntaxNode right = operand();
                left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = op.Operation, Left = left, Right = right, Offset = t.Start };
            }
            return left;
        }

        private SyntaxNode Not()
        {
            if (IsKeyword("Not"))
            {
                Token t = Next();
                Guard();
                SyntaxNode operand = Not();
                _depth--;
                return new SyntaxNode { Kind = SyntaxKind.Unary, Operation = ScalarOperation.Not, Left = operand, Offset = t.Start };
            }
            return Comparison();
        }

        private void Guard()
        {
            if (++_depth > MaxDepth)
            {
                throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
        }

        private SyntaxNode Comparison() => ComparisonSuffix(Concatenation());

        private SyntaxNode ComparisonSuffix(SyntaxNode left)
        {
            while (true)
            {
                Token t = Peek();
                if (t.Kind == TokenKind.Name && t.Text.Equals("ALike", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotModelledSyntax("NotModelled.Alike", "ALIKE, the ANSI-92 pattern operator (R-EXPR-1 PAT-01)");
                }
                if (t.Kind == TokenKind.Symbol && t.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
                {
                    Next();
                    ScalarOperation op = t.Text switch
                    {
                        "=" => ScalarOperation.Equal,
                        "<>" => ScalarOperation.NotEqual,
                        "<" => ScalarOperation.Less,
                        ">" => ScalarOperation.Greater,
                        "<=" => ScalarOperation.LessOrEqual,
                        _ => ScalarOperation.GreaterOrEqual,
                    };
                    Token q = Peek();
                    if (q.Kind == TokenKind.Name
                        && (q.Text.Equals("ANY", StringComparison.OrdinalIgnoreCase)
                            || q.Text.Equals("SOME", StringComparison.OrdinalIgnoreCase)
                            || q.Text.Equals("ALL", StringComparison.OrdinalIgnoreCase)))
                    {
                        int afterQ = SkipSpace(q.Start + q.Length);
                        if (afterQ < source.Length && source[afterQ] == '(')
                        {
                            Next(); // ANY / SOME / ALL
                            Next(); // (
                            object sub = ParseSubqueryAt(Position, q.Text);
                            CloseParenthesis(out int closeOffset);
                            left = new SyntaxNode
                            {
                                Kind = SyntaxKind.QuantifiedComparison,
                                Operation = op,
                                Left = left,
                                QuantifierAll = q.Text.Equals("ALL", StringComparison.OrdinalIgnoreCase),
                                Subquery = sub,
                                Offset = t.Start,
                                CloseOffset = closeOffset,
                            };
                            continue;
                        }
                    }
                    left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = op, Left = left, Right = Concatenation(), Offset = t.Start };
                    continue;
                }
                if (IsKeyword("Is"))
                {
                    Next();
                    bool negated = false;
                    if (IsKeyword("Not"))
                    {
                        Next();
                        negated = true;
                    }
                    if (!IsKeyword("Null"))
                    {
                        throw new SyntaxError(ExpressionMessages.MissingOperator);
                    }
                    Next();
                    left = new SyntaxNode { Kind = SyntaxKind.IsNull, Left = left, Negated = negated, Offset = t.Start };
                    continue;
                }
                if (IsKeyword("Not"))
                {
                    // x Not Like / Not In / Not Between: the Not belongs to the predicate.
                    Next();
                    left = ParsePredicateAfterNot(left, t.Start);
                    continue;
                }
                if (IsKeyword("Like"))
                {
                    Next();
                    left = new SyntaxNode { Kind = SyntaxKind.Like, Left = left, Right = Concatenation(), Negated = false, Offset = t.Start };
                    continue;
                }
                if (IsKeyword("In"))
                {
                    Next();
                    left = ParseIn(left, negated: false, t.Start);
                    continue;
                }
                if (IsKeyword("Between"))
                {
                    Next();
                    SyntaxNode low = Concatenation();
                    if (!IsKeyword("And"))
                    {
                        throw new SyntaxError(ExpressionMessages.MissingOperator);
                    }
                    Next();
                    SyntaxNode high = Concatenation();
                    left = new SyntaxNode { Kind = SyntaxKind.Between, Left = left, Items = [low, high], Negated = false, Offset = t.Start };
                    continue;
                }
                return left;
            }
        }

        private SyntaxNode ParsePredicateAfterNot(SyntaxNode left, int offset)
        {
            if (IsKeyword("Like"))
            {
                Next();
                return new SyntaxNode { Kind = SyntaxKind.Like, Left = left, Right = Concatenation(), Negated = true, Offset = offset };
            }
            if (IsKeyword("In"))
            {
                Next();
                return ParseIn(left, negated: true, offset);
            }
            if (IsKeyword("Between"))
            {
                Next();
                SyntaxNode low = Concatenation();
                if (!IsKeyword("And"))
                {
                    throw new SyntaxError(ExpressionMessages.MissingOperator);
                }
                Next();
                SyntaxNode high = Concatenation();
                return new SyntaxNode { Kind = SyntaxKind.Between, Left = left, Items = [low, high], Negated = true, Offset = offset };
            }
            throw new SyntaxError(ExpressionMessages.MissingOperator);
        }

        private SyntaxNode ParseIn(SyntaxNode left, bool negated, int offset)
        {
            if (!IsSymbol("("))
            {
                throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
            Next();
            Token first = Peek();
            if (first.Kind == TokenKind.Name && first.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                object sub = ParseSubqueryAt(first.Start, "SELECT");
                CloseParenthesis(out int closeOffset);
                return new SyntaxNode { Kind = SyntaxKind.InSubquery, Left = left, Negated = negated, Subquery = sub, Offset = offset, CloseOffset = closeOffset };
            }
            var items = new List<SyntaxNode> { Expression() };
            while (IsSymbol(","))
            {
                Next();
                items.Add(Expression());
            }
            CloseParenthesis();
            return new SyntaxNode { Kind = SyntaxKind.In, Left = left, Items = items, Negated = negated, Offset = offset };
        }

        /// <summary>
        /// The closing parenthesis; at the end "Missing ), ], or Item", at a comma "Syntax error (comma)" (r-sql-13b/
        /// sub-in-list-null-forms: <c>(a, g) IN ((1, 'x'))</c>), else "missing operator".
        /// </summary>
        private void CloseParenthesis() => CloseParenthesis(out _);

        private void CloseParenthesis(out int closeOffset)
        {
            if (!IsSymbol(")"))
            {
                Token t = Peek();
                throw new SyntaxError(t.Kind == TokenKind.End ? ExpressionMessages.MissingParenthesis
                    : t.Kind == TokenKind.Symbol && t.Text == "," ? ExpressionMessages.CommaSyntax : ExpressionMessages.MissingOperator);
            }
            closeOffset = Next().Start;
        }

        private SyntaxNode Concatenation() => Binary(Additive, "&");

        private SyntaxNode Additive() => Binary(Modulo, "+", "-");

        private SyntaxNode Modulo()
        {
            SyntaxNode left = IntegerDivision();
            while (IsKeyword("Mod"))
            {
                Token t = Next();
                left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = ScalarOperation.Modulo, Left = left, Right = IntegerDivision(), Offset = t.Start };
            }
            return left;
        }

        private SyntaxNode IntegerDivision() => Binary(Multiplicative, "\\");

        private SyntaxNode Multiplicative() => Binary(Power, "*", "/");

        private SyntaxNode Binary(Func<SyntaxNode> operand, params ReadOnlySpan<string> symbols)
        {
            SyntaxNode left = operand();
            while (Peek() is { Kind: TokenKind.Symbol } t && symbols.Contains(t.Text))
            {
                Next();
                ScalarOperation op = t.Text switch
                {
                    "&" => ScalarOperation.Concatenate,
                    "+" => ScalarOperation.Add,
                    "-" => ScalarOperation.Subtract,
                    "\\" => ScalarOperation.IntegerDivide,
                    "*" => ScalarOperation.Multiply,
                    _ => ScalarOperation.Divide,
                };
                left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = op, Left = left, Right = operand(), Offset = t.Start };
            }
            return left;
        }

        /// <summary>
        /// ^ over unary operands: ACE binds unary minus tighter than ^, unlike VBA (qualified.md "R-EXPR-1 Branch B, EXPR-RUNTIME":
        /// <c>-2 ^ 2</c> is 4, r-expr-1b/op-precedence); <c>2 ^ -2</c> takes the minus on its right operand.
        /// </summary>
        private SyntaxNode Power()
        {
            SyntaxNode left = Negation();
            while (IsSymbol("^"))
            {
                Token t = Next();
                SyntaxNode right = Negation();
                left = new SyntaxNode { Kind = SyntaxKind.Binary, Operation = ScalarOperation.Power, Left = left, Right = right, Offset = t.Start };
            }
            return left;
        }

        /// <summary>Unary minus (and plus): <c>20 -- c</c> is 20 - (-c).</summary>
        private SyntaxNode Negation()
        {
            if (IsSymbol("-") || IsSymbol("+"))
            {
                Token t = Next();
                Guard();
                SyntaxNode operand = Negation();
                _depth--;
                return t.Text == "+" ? operand : new SyntaxNode { Kind = SyntaxKind.Unary, Operation = ScalarOperation.Negate, Left = operand, Offset = t.Start };
            }
            return Primary();
        }

        private unsafe SyntaxNode Primary()
        {
            Token t = Next();
            switch (t.Kind)
            {
                case TokenKind.Number:
                    if (!ScalarLiterals.TryNumber(t.Text, out NativeVariant number))
                    {
                        // r-expr-1b/conv-range-edges: CDbl(1.8E308) is "Syntax error in number".
                        throw new SyntaxError(ExpressionMessages.NumberSyntax);
                    }
                    return new SyntaxNode { Kind = SyntaxKind.Literal, Literal = LiteralKind.Number, Value = number, Text = t.Text, Offset = t.Start };
                case TokenKind.Text:
                    {
                        string text = t.Text;
                        nint bstr = Bstrs.FromUnits(text.Length, text, static (span, s) => s.AsSpan().CopyTo(span));
                        Texts.Add(bstr);
                        return new SyntaxNode
                        {
                            Kind = SyntaxKind.Literal, Literal = LiteralKind.Text, Value = new NativeVariant { Vt = (ushort)CalcDb.Foundation.VarType.Bstr, Pointer = bstr },
                            Text = text, DoubledDelimiter = t.Doubled, Offset = t.Start,
                        };
                    }
                case TokenKind.Date:
                    if (!ScalarLiterals.TryDate(t.Text, out NativeVariant date))
                    {
                        throw new SyntaxError(ExpressionMessages.DateSyntax);
                    }
                    return new SyntaxNode { Kind = SyntaxKind.Literal, Literal = LiteralKind.Date, Value = date, Text = t.Text, Offset = t.Start };
                case TokenKind.Keyword when t.Text.Equals("True", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("False", StringComparison.OrdinalIgnoreCase):
                    return new SyntaxNode { Kind = SyntaxKind.Literal, Literal = LiteralKind.Boolean, Value = ScalarLiterals.Boolean(t.Text.Equals("True", StringComparison.OrdinalIgnoreCase)), Text = t.Text, Offset = t.Start };
                case TokenKind.Keyword when t.Text.Equals("Null", StringComparison.OrdinalIgnoreCase):
                    return new SyntaxNode { Kind = SyntaxKind.Literal, Literal = LiteralKind.Null, Value = ScalarLiterals.Null, Text = t.Text, Offset = t.Start };
                case TokenKind.Name:
                    if (t.Text.Equals("EXISTS", StringComparison.OrdinalIgnoreCase) && IsSymbol("(") && parseSubquery is not null)
                    {
                        Next(); // (
                        object sub = ParseSubqueryAt(Position, t.Text);
                        CloseParenthesis(out int closeOffset);
                        return new SyntaxNode { Kind = SyntaxKind.Exists, Subquery = sub, Offset = t.Start, CloseOffset = closeOffset };
                    }
                    if (t.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                        || (IsSymbol("(") && (t.Text.Equals("EXISTS", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("ANY", StringComparison.OrdinalIgnoreCase)
                            || t.Text.Equals("ALL", StringComparison.OrdinalIgnoreCase) || t.Text.Equals("SOME", StringComparison.OrdinalIgnoreCase))))
                    {
                        throw new NotModelledSyntax("NotModelled.Subquery", $"a subquery ({t.Text}): R-SQL-1 SQL-SUB, W-06/relational-spill");
                    }
                    if (t.Text.Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotModelledSyntax("NotModelled.Subquery", "EXISTS: R-SQL-1 SQL-SUB, W-06/relational-spill");
                    }
                    if (IsSymbol("("))
                    {
                        var call = new SyntaxNode { Kind = SyntaxKind.Call, Text = t.Text, Items = Arguments(out int closeOffset), Offset = t.Start, CloseOffset = closeOffset };
                        if (IsSymbol("("))
                        {
                            // r-expr-1b/route-mdb-battery: Split('a b', ' ')(1).
                            throw new SyntaxError(ExpressionMessages.InvalidQualifier);
                        }
                        return call;
                    }
                    return new SyntaxNode { Kind = SyntaxKind.Name, Text = t.Text, Offset = t.Start };
                case TokenKind.Symbol when t.Text == "(":
                    {
                        Token innerFirst = Peek();
                        if (innerFirst.Kind == TokenKind.Name && innerFirst.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                        {
                            object sub = ParseSubqueryAt(innerFirst.Start, "SELECT");
                            CloseParenthesis(out int closeOffset);
                            return new SyntaxNode { Kind = SyntaxKind.Subquery, Subquery = sub, Offset = t.Start, CloseOffset = closeOffset };
                        }
                        SyntaxNode inner = Expression();
                        CloseParenthesis();
                        return inner;
                    }
                case TokenKind.Symbol when t.Text == ";":
                    throw new SyntaxError(ExpressionMessages.SyntaxError);
                case TokenKind.Symbol when t.Text == ")":
                    // r-sql-13b/agg-distinct-forms: Count(DISTINCT *) is "Extra )"; the rule for a ) where an operand is due is not identified.
                    throw new NotModelledSyntax("NotModelled.ExtraParenthesis", "a ) where an operand is due");
                default:
                    throw new SyntaxError(ExpressionMessages.MissingOperator);
            }
        }

        /// <summary>A call's arguments: <c>()</c>, <c>(*)</c> (SQL Count; the binder admits it only there) or a list.</summary>
        private List<SyntaxNode> Arguments(out int closeOffset)
        {
            Next(); // (
            if (IsSymbol(")"))
            {
                closeOffset = Next().Start;
                return [];
            }
            if (IsSymbol("*"))
            {
                Token star = Next();
                CloseParenthesis(out closeOffset);
                return [new SyntaxNode { Kind = SyntaxKind.Star, Offset = star.Start }];
            }
            var items = new List<SyntaxNode> { Expression() };
            while (IsSymbol(","))
            {
                Next();
                items.Add(Expression());
            }
            CloseParenthesis(out closeOffset);
            return items;
        }

        // ---- Lexer ----

        private Token Lex()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
            {
                _position++;
            }
            int start = _position;
            if (_position >= source.Length)
            {
                return new Token(TokenKind.End, start, 0, "");
            }
            char c = source[_position];
            if (char.IsAsciiDigit(c) || (c == '.' && _position + 1 < source.Length && char.IsAsciiDigit(source[_position + 1])))
            {
                return LexNumber(start);
            }
            if (c is '\'' or '"')
            {
                return LexText(start, c);
            }
            if (c == '#')
            {
                int close = source.IndexOf('#', start + 1);
                if (close < 0)
                {
                    throw new SyntaxError(ExpressionMessages.DateSyntax);
                }
                _position = close + 1;
                return new Token(TokenKind.Date, start, _position - start, source[(start + 1)..close]);
            }
            if (c is '[' or '`' || char.IsLetter(c) || c == '_')
            {
                return LexName(start);
            }
            if (c is '?' or '@' or ':')
            {
                throw new NotModelledSyntax("NotModelled.ParameterMarker", $"'{c}' in an expression (r-def-expr-sql/sql-foreign-identifier-quoting)");
            }
            if (c is '<' or '>')
            {
                _position++;
                if (_position < source.Length && (source[_position] == '=' || (c == '<' && source[_position] == '>')))
                {
                    _position++;
                }
                return new Token(TokenKind.Symbol, start, _position - start, source[start.._position]);
            }
            _position++;
            return new Token(TokenKind.Symbol, start, 1, c.ToString());
        }

        private Token LexNumber(int start)
        {
            while (_position < source.Length && char.IsAsciiDigit(source[_position]))
            {
                _position++;
            }
            if (_position < source.Length && source[_position] == '.')
            {
                _position++;
                while (_position < source.Length && char.IsAsciiDigit(source[_position]))
                {
                    _position++;
                }
            }
            if (_position < source.Length && source[_position] is 'e' or 'E')
            {
                int p = _position + 1;
                if (p < source.Length && source[p] is '+' or '-')
                {
                    p++;
                }
                if (p < source.Length && char.IsAsciiDigit(source[p]))
                {
                    while (p < source.Length && char.IsAsciiDigit(source[p]))
                    {
                        p++;
                    }
                    _position = p;
                }
            }
            return new Token(TokenKind.Number, start, _position - start, source[start.._position]);
        }

        /// <summary>A string in either quote, the delimiter doubled inside; unterminated is "Syntax error in string".</summary>
        private Token LexText(int start, char quote)
        {
            var text = new System.Text.StringBuilder();
            bool doubled = false;
            int p = start + 1;
            while (true)
            {
                if (p >= source.Length)
                {
                    throw new SyntaxError(ExpressionMessages.StringSyntax);
                }
                char ch = source[p];
                if (ch == quote)
                {
                    if (p + 1 < source.Length && source[p + 1] == quote)
                    {
                        text.Append(quote);
                        doubled = true;
                        p += 2;
                        continue;
                    }
                    _position = p + 1;
                    return new Token(TokenKind.Text, start, _position - start, text.ToString(), doubled);
                }
                text.Append(ch);
                p++;
            }
        }

        /// <summary>
        /// A name: letters, digits and _, or a quoted name in brackets or backticks (qualified.md R-SQL-1 SQL-B, corrected:
        /// backticks quote names as brackets do, r-q3-sql/backtick-names); parts joined by '.' form one qualified name.
        /// </summary>
        private Token LexName(int start)
        {
            var name = new System.Text.StringBuilder();
            bool bracketed = false;
            while (true)
            {
                if (_position < source.Length && source[_position] is '[' or '`')
                {
                    int close = source.IndexOf(source[_position] == '[' ? ']' : '`', _position + 1);
                    if (close < 0)
                    {
                        throw new SyntaxError(ExpressionMessages.MissingParenthesis);
                    }
                    name.Append(source, _position + 1, close - _position - 1);
                    _position = close + 1;
                    bracketed = true;
                }
                else
                {
                    int s = _position;
                    while (_position < source.Length && (char.IsLetterOrDigit(source[_position]) || source[_position] == '_'))
                    {
                        _position++;
                    }
                    if (_position == s)
                    {
                        throw new SyntaxError(ExpressionMessages.MissingOperator);
                    }
                    if (_position < source.Length && source[_position] == '$')
                    {
                        _position++; // VBA's string form of a function name (Left$, Mid$, ChrW$)
                    }
                    name.Append(source, s, _position - s);
                }
                if (_position + 1 < source.Length && source[_position] is '.' or '!' && (source[_position + 1] is '[' or '`' || char.IsLetter(source[_position + 1]) || source[_position + 1] == '_'))
                {
                    // T.F, and the bang form Forms!F!x, which a query takes as one name (r-expr-1b/sandbox-access-hosted-names: 3061).
                    name.Append(source[_position]);
                    _position++;
                    bracketed = false;
                    continue;
                }
                break;
            }
            string text = name.ToString();
            TokenKind kind = !bracketed && !text.Contains('.') && Keywords.Contains(text) ? TokenKind.Keyword : TokenKind.Name;
            return new Token(kind, start, _position - start, text);
        }
    }
}