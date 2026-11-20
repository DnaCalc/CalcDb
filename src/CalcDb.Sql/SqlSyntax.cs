using CalcDb.Expressions;

namespace CalcDb.Sql;

/// <summary>An expression of a statement: its parse (the one expression grammar, II-10 §2) and its source span.</summary>
public sealed record Fragment(ExpressionSyntax Syntax, int Start, int End)
{
    public string Text(string source) => source[Start..End].TrimEnd();
}

/// <summary>The duplicate predicate of a SELECT (II-11 §11): ALL (the default), DISTINCT or DISTINCTROW.</summary>
public enum SelectPredicate : byte
{
    All,
    Distinct,
    DistinctRow,
}

/// <summary>An outer or inner join of two sources (II-11 §8).</summary>
public enum JoinKind : byte
{
    Inner,
    Left,
    Right,
}

/// <summary>A FROM source (II-11 §2 SourceSyntax): a named table or query, or a join of two sources.</summary>
public abstract record SourceSyntax(int Offset);

/// <summary>A table or saved query by name, with its alias when it has one.</summary>
public sealed record NamedSource(string Name, string? Alias, int Offset) : SourceSyntax(Offset)
{
    /// <summary>The name the statement's scope knows it by.</summary>
    public string ScopeName => Alias ?? Name;
}

/// <summary><c>left [INNER|LEFT|RIGHT] JOIN right ON condition</c>.</summary>
public sealed record JoinSource(JoinKind Kind, SourceSyntax Left, SourceSyntax Right, Fragment On, int Offset) : SourceSyntax(Offset);

/// <summary>A select-list item: <c>*</c>, <c>source.*</c>, or an expression with its alias.</summary>
public sealed record SelectItem(Fragment? Expression, bool Star, string? StarSource, string? Alias, int Offset);

/// <summary>An ORDER BY key.</summary>
public sealed record OrderItem(Fragment Expression, bool Descending);

/// <summary>TOP n or TOP n PERCENT (II-11 §12), with the number as written.</summary>
public readonly record struct TopClause(double Count, bool Percent, string Text);

/// <summary>
/// A parsed SELECT (II-11 §2 SelectSyntax: the source and the spans, no binding). Owns its expressions' literal texts
/// until <see cref="Dispose"/>; the binder takes over the ones its programs keep.
/// </summary>
public sealed class SelectSyntax : IDisposable
{
    internal SelectSyntax(string source)
    {
        Source = source;
    }

    public string Source { get; }

    public SelectPredicate Predicate { get; internal set; }

    public TopClause? Top { get; internal set; }

    public List<SelectItem> Items { get; } = [];

    /// <summary>The comma-separated FROM sources, in order.</summary>
    public List<SourceSyntax> From { get; } = [];

    public Fragment? Where { get; internal set; }

    public List<Fragment> GroupBy { get; } = [];

    public Fragment? Having { get; internal set; }

    public List<OrderItem> OrderBy { get; } = [];

    /// <summary>Every expression of the statement, for disposal.</summary>
    internal List<Fragment> Fragments { get; } = [];

    public void Dispose()
    {
        foreach (Fragment f in Fragments)
        {
            f.Syntax.Dispose();
        }
        Fragments.Clear();
    }
}

/// <summary>
/// The Access SELECT grammar (W-06/parser-binder-scan, II-11 §2), over the one expression grammar: the statement's words
/// and punctuation are read here, each expression by <see cref="ExpressionSyntax.ParseFragment"/>, which stops where a
/// clause word, a comma or the end follows. Covered: <c>SELECT [ALL|DISTINCT|DISTINCTROW] [TOP n [PERCENT]] items FROM
/// sources [WHERE e] [GROUP BY e, …] [HAVING e] [ORDER BY e [ASC|DESC], …] [;]</c>, items <c>*</c>, <c>s.*</c> and
/// <c>e [AS alias]</c>, sources a name with an optional alias, a comma list and <c>INNER|LEFT [OUTER]|RIGHT [OUTER] JOIN
/// … ON …</c>, a chain of joins only parenthesized (R-SQL-1 SQL-B: unparenthesised chains fail 3075). Other statements
/// and forms (PARAMETERS, UNION, TRANSFORM, subqueries, IN 'file') are named not-modelled cases.
/// </summary>
public static class SqlParser
{
    /// <summary>3141, the SELECT statement's own syntax error (r-sql-2-3/top-arguments: TOP 0, TOP -1).</summary>
    public const string SelectSyntaxError = "The SELECT statement includes a reserved word or an argument name that is misspelled or missing, or the punctuation is incorrect.";

    private static readonly HashSet<string> ClauseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "BY", "AS", "ON", "INNER", "LEFT", "RIGHT", "OUTER", "JOIN",
        "UNION", "ASC", "DESC", "IN", "TOP", "PERCENT", "DISTINCT", "DISTINCTROW", "ALL", "WITH", "PARAMETERS", "TRANSFORM", "PIVOT",
    };

    /// <summary>
    /// Parse a statement. The text ends at its first NUL (qualified.md "refusals of a SELECT's output"); 65534 units or
    /// more fail 3035, and between 65001 and 65533 the limit is not identified (R-SQL-1 SQL-B limits).
    /// </summary>
    public static SelectSyntax? Parse(string source, out SqlFailure? failure)
    {
        int nul = source.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            source = source[..nul];
        }
        if (source.Length >= 65534)
        {
            failure = SqlFailure.Ace(SqlPhase.Parse, 3035, "System resource exceeded.");
            return null;
        }
        if (source.Length > 65000)
        {
            failure = SqlFailure.Open(SqlPhase.Parse, "NotModelled.StatementLength", $"{source.Length} units: the length limit lies between 65000 and 65534");
            return null;
        }
        var p = new Parser(source);
        var statement = new SelectSyntax(source);
        failure = p.Select(statement);
        if (failure is null && p.BadName)
        {
            failure = SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
        }
        if (failure is not null)
        {
            statement.Dispose();
            return null;
        }
        return statement;
    }

    private sealed class Parser(string s)
    {
        private int _p;

        private static SqlFailure Open(string name, string detail) => SqlFailure.Open(SqlPhase.Parse, name, detail);

        /// <summary>A bracketed name followed by another ']' was read.</summary>
        public bool BadName { get; private set; }

        private void Skip()
        {
            while (_p < s.Length && char.IsWhiteSpace(s[_p]))
            {
                _p++;
            }
        }

        private bool AtEnd()
        {
            Skip();
            return _p >= s.Length;
        }

        /// <summary>The word at the position (letters, digits, _), without consuming it.</summary>
        private string? PeekWord()
        {
            Skip();
            int q = _p;
            if (q >= s.Length || !(char.IsLetter(s[q]) || s[q] == '_'))
            {
                return null;
            }
            while (q < s.Length && (char.IsLetterOrDigit(s[q]) || s[q] == '_'))
            {
                q++;
            }
            return s[_p..q];
        }

        private bool IsWord(string word) => string.Equals(PeekWord(), word, StringComparison.OrdinalIgnoreCase);

        private bool TakeWord(string word)
        {
            if (!IsWord(word))
            {
                return false;
            }
            _p += word.Length;
            return true;
        }

        private bool IsSymbol(char c)
        {
            Skip();
            return _p < s.Length && s[_p] == c;
        }

        private bool TakeSymbol(char c)
        {
            if (!IsSymbol(c))
            {
                return false;
            }
            _p++;
            return true;
        }

        /// <summary>A name: <c>[any text]</c> or a word that is not a clause word.</summary>
        private string? Name(bool allowClauseWord = false)
        {
            Skip();
            if (_p < s.Length && s[_p] == '[')
            {
                int close = s.IndexOf(']', _p + 1);
                if (close < 0)
                {
                    return null;
                }
                string name = s[(_p + 1)..close];
                _p = close + 1;
                if (_p < s.Length && s[_p] == ']')
                {
                    // r-sql-1/b-brackets: [x]]y] is 3141 (no escaped bracket).
                    BadName = true;
                }
                return name;
            }
            string? word = PeekWord();
            if (word is null || (!allowClauseWord && ClauseWords.Contains(word)))
            {
                return null;
            }
            _p += word.Length;
            return word;
        }

        private SqlFailure? Expression(SelectSyntax statement, out Fragment? fragment)
        {
            Skip();
            int start = _p;
            ExpressionSyntax syntax = ExpressionSyntax.ParseFragment(s, start, out int end);
            if (syntax.Failure is { } f)
            {
                fragment = null;
                return !f.IsNotModelled && Depth(start, s.Length) >= 31
                    ? new SqlFailure(SqlPhase.Parse, 3075, $"Expression too complex{SqlFailure.QueryExpression}{s[start..].TrimEnd()}'.") { ExcerptExact = false }
                    : SqlFailure.Of(f, SqlPhase.Parse, s, start, end);
            }
            fragment = new Fragment(syntax, start, end);
            statement.Fragments.Add(fragment);
            _p = end;
            return Complexity(fragment);
        }

        /// <summary>
        /// The expression limits of R-SQL-1 SQL-B: parentheses nested 31 deep fail 3075 "Expression too complex" and 28
        /// open; an AND chain of 97 opens, 98 failed 3072 and 100 3360. Between the recorded points the limit is named.
        /// </summary>
        private int Depth(int start, int end)
        {
            int depth = 0, deepest = 0;
            for (int i = start; i < end; i++)
            {
                depth += s[i] == '(' ? 1 : s[i] == ')' ? -1 : 0;
                deepest = Math.Max(deepest, depth);
            }
            return deepest;
        }

        private SqlFailure? Complexity(Fragment fragment)
        {
            int depth = 0, deepest = 0, ands = 0;
            char quote = '\0';
            for (int i = fragment.Start; i < fragment.End; i++)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    quote = c == quote ? '\0' : quote;
                    continue;
                }
                switch (c)
                {
                    case '\'' or '"':
                        quote = c;
                        break;
                    case '(':
                        deepest = Math.Max(deepest, ++depth);
                        break;
                    case ')':
                        depth--;
                        break;
                    case 'A' or 'a' when i + 3 <= s.Length && s.AsSpan(i, 3).Equals("AND", StringComparison.OrdinalIgnoreCase)
                        && (i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (i + 3 == s.Length || !char.IsLetterOrDigit(s[i + 3])):
                        ands++;
                        break;
                }
            }
            if (deepest >= 31)
            {
                return new SqlFailure(SqlPhase.Parse, 3075, $"Expression too complex{SqlFailure.QueryExpression}{fragment.Text(s)}'.") { ExcerptExact = false };
            }
            if (deepest > 28)
            {
                return Open("NotModelled.ExpressionDepth", $"parentheses {deepest} deep: the limit lies between 28 and 31");
            }
            return ands > 97 ? Open("NotModelled.AndChain", $"{ands} ANDs: 97 open, 98 failed 3072 and 100 3360") : null;
        }

        /// <summary>An expression followed by something its clause cannot continue with: ACE reads on and reports it.</summary>
        private SqlFailure MissingOperator(int start)
        {
            Skip();
            int stop = _p;
            while (stop < s.Length && !char.IsWhiteSpace(s[stop]))
            {
                stop++;
            }
            return new SqlFailure(SqlPhase.Parse, 3075, $"Syntax error (missing operator){SqlFailure.QueryExpression}{s[start..stop]}'.") { ExcerptExact = false };
        }

        public SqlFailure? Select(SelectSyntax statement)
        {
            if (!TakeWord("SELECT"))
            {
                string? word = PeekWord();
                return Open("NotModelled.Statement", $"a statement starting {word ?? "with punctuation"}: only SELECT is parsed in W-06/parser-binder-scan");
            }
            if (TakeWord("DISTINCTROW"))
            {
                statement.Predicate = SelectPredicate.DistinctRow;
            }
            else if (TakeWord("DISTINCT"))
            {
                statement.Predicate = SelectPredicate.Distinct;
            }
            else
            {
                TakeWord("ALL");
            }
            if (TakeWord("TOP"))
            {
                if (Top(statement) is { } topFailure)
                {
                    return topFailure;
                }
            }
            do
            {
                if (Item(statement) is { } itemFailure)
                {
                    return itemFailure;
                }
            }
            while (TakeSymbol(','));
            if (!TakeWord("FROM"))
            {
                return AtEnd() ? Open("NotModelled.NoFrom", "a SELECT without FROM") : MissingOperator(statement.Items[^1].Offset);
            }
            do
            {
                if (Source(statement, out SourceSyntax? source) is { } sourceFailure)
                {
                    return sourceFailure;
                }
                statement.From.Add(source!);
            }
            while (TakeSymbol(','));
            if (TakeWord("WHERE"))
            {
                if (Clause(statement, out Fragment? where, "GROUP", "HAVING", "ORDER") is { } f)
                {
                    return f;
                }
                statement.Where = where;
            }
            if (TakeWord("GROUP"))
            {
                if (!TakeWord("BY"))
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3132, "Syntax error in GROUP BY clause.");
                }
                do
                {
                    if (Clause(statement, out Fragment? key, "HAVING", "ORDER") is { } f)
                    {
                        return f;
                    }
                    statement.GroupBy.Add(key!);
                }
                while (TakeSymbol(','));
            }
            if (TakeWord("HAVING"))
            {
                if (Clause(statement, out Fragment? having, "ORDER") is { } f)
                {
                    return f;
                }
                statement.Having = having;
            }
            if (TakeWord("ORDER"))
            {
                if (!TakeWord("BY"))
                {
                    return Open("NotModelled.SqlSyntax", "ORDER without BY (ACE's answer not recorded)");
                }
                do
                {
                    if (Clause(statement, out Fragment? key, "ASC", "DESC") is { } f)
                    {
                        return f;
                    }
                    bool descending = TakeWord("DESC");
                    if (!descending)
                    {
                        TakeWord("ASC");
                    }
                    statement.OrderBy.Add(new OrderItem(key!, descending));
                }
                while (TakeSymbol(','));
            }
            TakeSymbol(';');
            if (!AtEnd())
            {
                string? word = PeekWord();
                return word is not null && word.Equals("UNION", StringComparison.OrdinalIgnoreCase)
                    ? Open("NotModelled.Union", "UNION (W-06/relational-spill)")
                    : Open("NotModelled.SqlSyntax", $"text after the statement at {_p} (ACE's answer not recorded)");
            }
            return null;
        }

        /// <summary>
        /// TOP n [PERCENT] (qualified.md R-SQL-3 SQL-TOP: <c>+2</c>, <c>2E0</c>, <c>2.0</c> and 2147483647 are accepted; 0,
        /// -1, <c>-0</c>, <c>0 PERCENT</c> and more than 100 PERCENT are 3141; <c>TOP 2.5</c> returns 2). Forms the records
        /// do not decide (<c>&amp;H2</c>, expressions, parameters) are named.
        /// </summary>
        private SqlFailure? Top(SelectSyntax statement)
        {
            Skip();
            int start = _p;
            while (_p < s.Length && (char.IsAsciiDigit(s[_p]) || s[_p] is '.' or '+' or '-' or 'e' or 'E'))
            {
                if (s[_p] is '+' or '-' && _p > start && s[_p - 1] is not ('e' or 'E'))
                {
                    break;
                }
                _p++;
            }
            string text = s[start.._p];
            string digits = text.TrimStart('+', '-');
            if (digits.Length == 0 || !char.IsAsciiDigit(digits[0]) || !(_p >= s.Length || char.IsWhiteSpace(s[_p]))
                || !double.TryParse(digits, System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out double value))
            {
                return Open("NotModelled.TopArgument", $"TOP {text}{(_p < s.Length ? s[_p].ToString() : "")}…: this form is not decided by the records");
            }
            bool percent = TakeWord("PERCENT");
            if (value <= 0 || text.StartsWith('-') || (percent && value > 100) || value > int.MaxValue)
            {
                return SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
            }
            statement.Top = new TopClause(value, percent, text);
            return null;
        }

        private SqlFailure? Item(SelectSyntax statement)
        {
            Skip();
            int offset = _p;
            if (TakeSymbol('*'))
            {
                statement.Items.Add(new SelectItem(null, true, null, null, offset));
                return null;
            }
            int save = _p;
            if (Name() is { } qualifier && TakeSymbol('.') && TakeSymbol('*'))
            {
                statement.Items.Add(new SelectItem(null, true, qualifier, null, offset));
                return null;
            }
            _p = save;
            if (Expression(statement, out Fragment? expression) is { } f)
            {
                return f;
            }
            string? alias = null;
            if (TakeWord("AS"))
            {
                alias = Name(allowClauseWord: true);
                if (alias is null || BadName)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
                }
            }
            statement.Items.Add(new SelectItem(expression, false, null, alias, offset));
            if (!IsSymbol(',') && !IsWord("FROM") && !AtEnd())
            {
                return MissingOperator(offset);
            }
            return null;
        }

        /// <summary>A clause expression that one of <paramref name="followers"/>, a comma, <c>;</c> or the end must follow.</summary>
        private SqlFailure? Clause(SelectSyntax statement, out Fragment? fragment, params string[] followers)
        {
            Skip();
            int start = _p;
            if (Expression(statement, out fragment) is { } f)
            {
                return f;
            }
            if (AtEnd() || IsSymbol(',') || IsSymbol(';') || followers.Any(IsWord))
            {
                return null;
            }
            string? word = PeekWord();
            if (word is not null && word.Equals("UNION", StringComparison.OrdinalIgnoreCase))
            {
                return Open("NotModelled.Union", "UNION (W-06/relational-spill)");
            }
            return MissingOperator(start);
        }

        /// <summary>A FROM source: a primary, then joins; a chain of two joins needs parentheses around the first.</summary>
        private SqlFailure? Source(SelectSyntax statement, out SourceSyntax? source)
        {
            if (Primary(statement, out source) is { } f)
            {
                return f;
            }
            while (JoinWord() is { } kind)
            {
                int at = _p;
                if (Primary(statement, out SourceSyntax? right) is { } rf)
                {
                    return rf;
                }
                if (!TakeWord("ON"))
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
                }
                Skip();
                int onStart = _p;
                if (Expression(statement, out Fragment? on) is { } of)
                {
                    return of;
                }
                if (JoinWord(peekOnly: true) is not null)
                {
                    // r-sql-13b/join-nesting-four-tables: the ON expression reads on into the next JOIN.
                    return MissingOperator(onStart);
                }
                source = new JoinSource(kind, source!, right!, on!, at);
            }
            return null;
        }

        /// <summary><c>INNER JOIN</c>, <c>LEFT [OUTER] JOIN</c> or <c>RIGHT [OUTER] JOIN</c>, consumed unless <paramref name="peekOnly"/>.</summary>
        private JoinKind? JoinWord(bool peekOnly = false)
        {
            int save = _p;
            JoinKind? kind = TakeWord("INNER") ? JoinKind.Inner : TakeWord("LEFT") ? JoinKind.Left : TakeWord("RIGHT") ? JoinKind.Right : null;
            if (kind is JoinKind.Left or JoinKind.Right)
            {
                TakeWord("OUTER");
            }
            if (kind is null || !TakeWord("JOIN"))
            {
                _p = save;
                return null;
            }
            if (peekOnly)
            {
                _p = save;
            }
            return kind;
        }

        private SqlFailure? Primary(SelectSyntax statement, out SourceSyntax? source)
        {
            Skip();
            int offset = _p;
            source = null;
            if (TakeSymbol('('))
            {
                if (IsWord("SELECT"))
                {
                    return Open("NotModelled.Subquery", "a subquery in FROM");
                }
                if (Source(statement, out source) is { } f)
                {
                    return f;
                }
                return TakeSymbol(')') ? null : SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
            }
            string? name = Name();
            if (name is null)
            {
                return SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
            }
            if (IsWord("IN"))
            {
                return Open("NotModelled.ExternalSource", "FROM … IN 'file' (II-11 §2 external IN)");
            }
            string? alias = null;
            if (TakeWord("AS"))
            {
                alias = Name(allowClauseWord: true);
                if (alias is null)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
                }
            }
            else if (Name() is { } bare)
            {
                alias = bare;
            }
            source = new NamedSource(name, alias, offset);
            return null;
        }
    }
}