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

/// <summary><c>(SELECT …) [AS alias]</c>, a derived table in FROM (r-sort-3/equality-accdb, r-sort-3b/memo-equality-sql).</summary>
public sealed record SubquerySource(SelectSyntax Query, string? Alias, int Offset) : SourceSyntax(Offset)
{
    public string ScopeName => Alias ?? "";
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
/// A PARAMETERS declaration (II-11 §2 parameter_declarations; qualified.md R-SQL-3 SQL-META parameters): its name as
/// written, its DAO type (DataTypeEnum, Parameter.Type) and the Field.Type of a column that projects it bare, which is
/// the same except for BigInt (16, Parameter.Type 19) and Value (0, Parameter.Type 10; not identified, null)
/// (r-sql-13b/params-declared-type-names).
/// </summary>
public sealed record ParameterDeclaration(string Name, int DaoType, int? FieldType, int? TextSize = null);

/// <summary>A later arm of a UNION with the operator before it (II-11 §15: each UNION/ALL token kept with its arm).</summary>
public readonly record struct UnionArm(bool All, SelectSyntax Select);

/// <summary>
/// A parsed statement (II-11 §2 QuerySyntax: the declarations, the body, the source and the spans, no binding): a SELECT,
/// whose first arm is this object's own clauses, with its later UNION arms and the union's final ORDER BY. Owns its
/// expressions' literal texts until <see cref="Dispose"/>; the binder takes over the ones its programs keep.
/// </summary>
public sealed class SelectSyntax : IDisposable
{
    internal SelectSyntax(string source)
    {
        Source = source;
    }

    public string Source { get; }

    /// <summary>The PARAMETERS declarations, in order (a statement's, never an arm's).</summary>
    public List<ParameterDeclaration> Parameters { get; } = [];

    /// <summary>The arms after the first, in order; empty for a plain SELECT.</summary>
    public List<UnionArm> Unions { get; } = [];

    /// <summary>The ORDER BY of a union, over its output (written after the last arm).</summary>
    public List<OrderItem> UnionOrderBy { get; } = [];

    public bool IsUnion => Unions.Count > 0;

    public SelectPredicate Predicate { get; internal set; }

    public TopClause? Top { get; internal set; }

    public List<SelectItem> Items { get; } = [];

    /// <summary>The target table of a make-table <c>SELECT ... INTO target FROM ...</c>, or null.</summary>
    public string? IntoTarget { get; internal set; }

    /// <summary>Whether <c>WITH OWNERACCESS OPTION</c> appeared at the end of the statement.</summary>
    public bool WithOwnerAccess { get; internal set; }

    /// <summary>The comma-separated FROM sources, in order.</summary>
    public List<SourceSyntax> From { get; } = [];

    public Fragment? Where { get; internal set; }

    public List<Fragment> GroupBy { get; } = [];

    public Fragment? Having { get; internal set; }

    public List<OrderItem> OrderBy { get; } = [];

    /// <summary>Source span of this SELECT arm (used by UNION definition storage, r-q39).</summary>
    internal int ArmStart { get; set; }

    internal int ArmEnd { get; set; }

    internal bool ParenthesizedArm { get; set; }

    /// <summary>Every expression of the statement, for disposal.</summary>
    internal List<Fragment> Fragments { get; } = [];

    public void Dispose()
    {
        foreach (Fragment f in Fragments)
        {
            f.Syntax.Dispose();
        }
        Fragments.Clear();
        foreach (SourceSyntax source in From)
        {
            DisposeSource(source);
        }
        foreach (UnionArm arm in Unions)
        {
            arm.Select.Dispose();
        }
    }

    private static void DisposeSource(SourceSyntax source)
    {
        switch (source)
        {
            case SubquerySource sub:
                sub.Query.Dispose();
                break;
            case JoinSource join:
                DisposeSource(join.Left);
                DisposeSource(join.Right);
                break;
        }
    }
}

/// <summary>
/// A parsed Access crosstab query (<c>TRANSFORM expr [AS alias] SELECT ... FROM ... GROUP BY ... PIVOT expr [IN (...)]</c>,
/// r-q39 crosstab-accdb). Owns its <see cref="Select"/> and its expression fragments until <see cref="Dispose"/>.
/// </summary>
internal sealed class CrosstabSyntax(Fragment transform, string? transformAlias, SelectSyntax select, Fragment pivot, bool hasPivotIn) : IDisposable
{
    public Fragment Transform { get; } = transform;

    public string? TransformAlias { get; } = transformAlias;

    public SelectSyntax Select { get; } = select;

    public Fragment Pivot { get; } = pivot;

    public bool HasPivotIn { get; } = hasPivotIn;

    public void Dispose() => Select.Dispose();
}

/// <summary>
/// The Access SELECT grammar (W-06/parser-binder-scan, II-11 §2), over the one expression grammar: the statement's words
/// and punctuation are read here, each expression by <see cref="ExpressionSyntax.ParseFragment"/>, which stops where a
/// clause word, a comma or the end follows. Covered: <c>SELECT [ALL|DISTINCT|DISTINCTROW] [TOP n [PERCENT]] items FROM
/// sources [WHERE e] [GROUP BY e, …] [HAVING e] [ORDER BY e [ASC|DESC], …] [;]</c>, items <c>*</c>, <c>s.*</c> and
/// <c>e [AS alias]</c>, sources a name with an optional alias, a comma list and <c>INNER|LEFT [OUTER]|RIGHT [OUTER] JOIN
/// … ON …</c>, a chain of joins only parenthesized (R-SQL-1 SQL-B: unparenthesised chains fail 3075); a leading
/// <c>PARAMETERS name type, …;</c>; arms joined by <c>UNION [ALL]</c>, each optionally parenthesized, with the union's
/// ORDER BY after the last. Other statements and forms (TRANSFORM, subqueries, IN 'file') are named not-modelled cases.
/// </summary>
public static class SqlParser
{
    /// <summary>3141, the SELECT statement's own syntax error (r-sql-2-3/top-arguments: TOP 0, TOP -1; r-q25-nz-3141/baseline, alias-int-alone: unbracketed reserved word after AS).</summary>
    public const string SelectSyntaxError = "The SELECT statement includes a reserved word or an argument name that is misspelled or missing, or the punctuation is incorrect.";

    /// <summary>3131, the FROM clause syntax error (r-q25-nz-3141/alias-int-positions, alias-from-words, alias-more-words; r-def-expr-sql/sql-insert-values-forms; r-sql-13b/union-syntax-forms).</summary>
    public const string FromSyntaxError = "Syntax error in FROM clause.";

    /// <summary>3450, incomplete query clause (r-q25-nz-3141/alias-from-words #107: FROM Mx AS UNION).</summary>
    public const string IncompleteQueryClause = "Syntax error in query. Incomplete query clause.";

    /// <summary>3139 (r-sql-13b/params-declaration-syntax, params-declared-type-names).</summary>
    public const string ParameterClauseError = "Syntax error in PARAMETER clause.";

    /// <summary>3129 (r-sql-13b/params-declaration-syntax: two PARAMETERS clauses; union-syntax-forms: UNION DISTINCT, UNION CORRESPONDING).</summary>
    public const string InvalidStatement = "Invalid SQL statement; expected 'DELETE', 'INSERT', 'PROCEDURE', 'SELECT', or 'UPDATE'.";

    /// <summary>
    /// The declared type names (qualified.md R-SQL-3 SQL-META parameters, r-sql-13b/params-declared-type-names): their
    /// DAO types, or 0 for the names ACE refuses with 3139.
    /// </summary>
    private static readonly Dictionary<string, int> ParameterTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bit"] = 1, ["Byte"] = 2, ["Short"] = 3, ["Long"] = 4, ["Integer"] = 4, ["Int"] = 4, ["Currency"] = 5,
        ["IEEESingle"] = 6, ["Single"] = 6, ["Real"] = 6, ["IEEEDouble"] = 7, ["Double"] = 7, ["Float"] = 7, ["Numeric"] = 7,
        ["DateTime"] = 8, ["Binary"] = 9, ["Text"] = 10, ["Char"] = 10, ["VarChar"] = 10, ["Value"] = 10, ["LongBinary"] = 11,
        ["LongText"] = 12, ["Guid"] = 15, ["BigInt"] = 19, ["DateTime2"] = 20,
        ["Decimal"] = 0, ["Memo"] = 0, ["Counter"] = 0, ["Boolean"] = 0, ["Variant"] = 0, ["TinyInt"] = 0, ["DateTimeExtended"] = 0,
    };

    [Flags]
    private enum WordRole : byte
    {
        None = 0,

        /// <summary>Stops bare names (<c>s.*</c>, <c>FROM table</c>, bare <c>FROM t alias</c>).</summary>
        Clause = 1,

        /// <summary>
        /// Refused unbracketed after <c>AS</c> in a SELECT list (3141) and in <c>FROM ... AS</c> (3131, except <c>UNION</c> 3450):
        /// exactly the 66 words refused across r-q25-nz-3141/alias-type-words (24), r-q25-nz-3141/alias-clause-words (31),
        /// r-q25-nz-3141/alias-from-words, and r-q25-nz-3141/alias-more-words (11). The 39 accepted words and unprobed words
        /// do not carry this flag.
        /// </summary>
        ReservedAlias = 2,
    }

    private static readonly Dictionary<string, WordRole> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // 27 clause words, all refused after AS (24 in r-q25-nz-3141/alias-clause-words + 3 in alias-more-words / alias-from-words):
        ["SELECT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["FROM"] = WordRole.Clause | WordRole.ReservedAlias,
        ["WHERE"] = WordRole.Clause | WordRole.ReservedAlias,
        ["GROUP"] = WordRole.Clause | WordRole.ReservedAlias,
        ["HAVING"] = WordRole.Clause | WordRole.ReservedAlias,
        ["ORDER"] = WordRole.Clause | WordRole.ReservedAlias,
        ["BY"] = WordRole.Clause | WordRole.ReservedAlias,
        ["AS"] = WordRole.Clause | WordRole.ReservedAlias,
        ["ON"] = WordRole.Clause | WordRole.ReservedAlias,
        ["INNER"] = WordRole.Clause | WordRole.ReservedAlias,
        ["LEFT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["RIGHT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["JOIN"] = WordRole.Clause | WordRole.ReservedAlias,
        ["UNION"] = WordRole.Clause | WordRole.ReservedAlias,
        ["ASC"] = WordRole.Clause | WordRole.ReservedAlias,
        ["DESC"] = WordRole.Clause | WordRole.ReservedAlias,
        ["IN"] = WordRole.Clause | WordRole.ReservedAlias,
        ["TOP"] = WordRole.Clause | WordRole.ReservedAlias,
        ["DISTINCT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["DISTINCTROW"] = WordRole.Clause | WordRole.ReservedAlias,
        ["ALL"] = WordRole.Clause | WordRole.ReservedAlias,
        ["WITH"] = WordRole.Clause | WordRole.ReservedAlias,
        ["TRANSFORM"] = WordRole.Clause | WordRole.ReservedAlias,
        ["PIVOT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["OUTER"] = WordRole.Clause | WordRole.ReservedAlias,
        ["PERCENT"] = WordRole.Clause | WordRole.ReservedAlias,
        ["PARAMETERS"] = WordRole.Clause | WordRole.ReservedAlias,

        // 7 additional clause/DML words refused in r-q25-nz-3141/alias-clause-words and alias-from-words:
        ["INTO"] = WordRole.ReservedAlias,
        ["TABLE"] = WordRole.ReservedAlias,
        ["SET"] = WordRole.ReservedAlias,
        ["VALUES"] = WordRole.ReservedAlias,
        ["UPDATE"] = WordRole.ReservedAlias,
        ["DELETE"] = WordRole.ReservedAlias,
        ["INSERT"] = WordRole.ReservedAlias,

        // 24 type words refused in r-q25-nz-3141/alias-type-words and alias-from-words:
        ["INT"] = WordRole.ReservedAlias,
        ["INTEGER"] = WordRole.ReservedAlias,
        ["LONG"] = WordRole.ReservedAlias,
        ["SHORT"] = WordRole.ReservedAlias,
        ["BYTE"] = WordRole.ReservedAlias,
        ["SINGLE"] = WordRole.ReservedAlias,
        ["DOUBLE"] = WordRole.ReservedAlias,
        ["CURRENCY"] = WordRole.ReservedAlias,
        ["TEXT"] = WordRole.ReservedAlias,
        ["DATE"] = WordRole.ReservedAlias,
        ["DATETIME"] = WordRole.ReservedAlias,
        ["YESNO"] = WordRole.ReservedAlias,
        ["BIT"] = WordRole.ReservedAlias,
        ["BINARY"] = WordRole.ReservedAlias,
        ["GUID"] = WordRole.ReservedAlias,
        ["CHAR"] = WordRole.ReservedAlias,
        ["VARCHAR"] = WordRole.ReservedAlias,
        ["NUMERIC"] = WordRole.ReservedAlias,
        ["FLOAT"] = WordRole.ReservedAlias,
        ["REAL"] = WordRole.ReservedAlias,
        ["VALUE"] = WordRole.ReservedAlias,
        ["STRING"] = WordRole.ReservedAlias,
        ["NUMBER"] = WordRole.ReservedAlias,
        ["TIMESTAMP"] = WordRole.ReservedAlias,

        // 8 additional words refused in r-q25-nz-3141/alias-more-words (alongside OUTER, PERCENT, PARAMETERS above):
        ["OWNERACCESS"] = WordRole.ReservedAlias,
        ["TIME"] = WordRole.ReservedAlias,
        ["LONGTEXT"] = WordRole.ReservedAlias,
        ["LONGBINARY"] = WordRole.ReservedAlias,
        ["VARBINARY"] = WordRole.ReservedAlias,
        ["MONEY"] = WordRole.ReservedAlias,
        ["SMALLINT"] = WordRole.ReservedAlias,
        ["UNIQUE"] = WordRole.ReservedAlias,
    };

    private static bool HasRole(string word, WordRole role) =>
        role != WordRole.None && ReservedWords.TryGetValue(word, out WordRole flags) && (flags & role) != 0;

    /// <summary>
    /// Parse a statement. The text ends at its first NUL (qualified.md "refusals of a SELECT's output"); 65534 units or
    /// more fail 3035, and between 65001 and 65533 the limit is not identified (R-SQL-1 SQL-B limits).
    /// </summary>
    public static SelectSyntax? Parse(string source, out SqlFailure? failure)
    {
        if (!NormalizeSource(ref source, out failure))
        {
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

    internal static bool NormalizeSource(ref string source, out SqlFailure? failure)
    {
        int nul = source.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
        {
            source = source[..nul];
        }
        if (source.Length >= 65534)
        {
            failure = SqlFailure.Ace(SqlPhase.Parse, 3035, "System resource exceeded.");
            return false;
        }
        if (source.Length > 65000)
        {
            failure = SqlFailure.Open(SqlPhase.Parse, "NotModelled.StatementLength", $"{source.Length} units: the length limit lies between 65000 and 65534");
            return false;
        }
        failure = null;
        return true;
    }

    internal static SqlFailure? ParseLeadingParameters(string source, ref int position, List<ParameterDeclaration> parameters)
    {
        var p = new Parser(source, position);
        var scratch = new SelectSyntax(source);
        try
        {
            SqlFailure? failure = p.LeadingParameters(scratch);
            if (failure is not null)
            {
                return failure;
            }
            parameters.AddRange(scratch.Parameters);
            position = p.Position;
            return null;
        }
        finally
        {
            scratch.Dispose();
        }
    }

    /// <summary>
    /// Parse the SELECT body of an <c>INSERT INTO … SELECT …</c> starting at <paramref name="position"/>: handles both
    /// standard SELECT with FROM and FROM-less constant SELECT (<c>INSERT INTO U (ID, K) SELECT 602, 'y'</c>,
    /// r-def-expr-sql/sql-insert-values-forms), refuses UNION with 3131 (same record), and leaves <paramref name="position"/>
    /// before any trailing <c>WITH OWNERACCESS OPTION</c> or <c>;</c>.
    /// </summary>
    internal static SelectSyntax? ParseInsertSelect(string source, ref int position, out bool hasFrom, out SqlFailure? failure)
    {
        var p = new Parser(source, position);
        var statement = new SelectSyntax(source);
        failure = p.InsertSelectBody(statement, out hasFrom);
        if (failure is null && p.BadName)
        {
            failure = SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
        }
        if (failure is not null)
        {
            statement.Dispose();
            return null;
        }
        position = p.Position;
        return statement;
    }

    /// <summary>
    /// Parse a SELECT, SELECT INTO or UNION statement for saved-query definition compilation (<see cref="QueryDefinitionCompiler"/>):
    /// reuses the one SELECT grammar while also accepting <c>SELECT ... INTO target FROM ...</c> and recognizing trailing
    /// <c>WITH OWNERACCESS OPTION</c> so the definition compiler can refuse undecided clauses explicitly by name.
    /// </summary>
    internal static SelectSyntax? ParseForDefinition(string source, out SqlFailure? failure)
    {
        if (!NormalizeSource(ref source, out failure))
        {
            return null;
        }
        var p = new Parser(source);
        var statement = new SelectSyntax(source);
        failure = p.Select(statement, allowInto: true, allowWith: true);
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

    /// <summary>
    /// Parse a single-table <c>TRANSFORM expr [AS alias] SELECT ... FROM ... GROUP BY ... PIVOT expr [;]</c> statement
    /// for saved-query definition compilation (<see cref="QueryDefinitionCompiler"/>; r-q39 crosstab-accdb).
    /// </summary>
    internal static CrosstabSyntax? ParseCrosstab(string source, out SqlFailure? failure)
    {
        if (!NormalizeSource(ref source, out failure))
        {
            return null;
        }
        var p = new Parser(source);
        var statement = new SelectSyntax(source);
        failure = p.Crosstab(statement, out Fragment? transform, out string? transformAlias, out Fragment? pivot, out bool hasPivotIn);
        if (failure is null && p.BadName)
        {
            failure = SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
        }
        if (failure is not null)
        {
            statement.Dispose();
            return null;
        }
        return new CrosstabSyntax(transform!, transformAlias, statement, pivot!, hasPivotIn);
    }

    /// <summary>
    /// Parse a nested SQL <c>SELECT</c> (including <c>UNION</c> arms) inside an expression (<c>(SELECT …)</c>,
    /// <c>EXISTS (SELECT …)</c>, <c>IN (SELECT …)</c>, <c>ANY</c>/<c>ALL (SELECT …)</c>). Used as the
    /// <see cref="SubqueryParser"/> callback for <see cref="ExpressionSyntax.Parse"/> and
    /// <see cref="ExpressionSyntax.ParseFragment"/>.
    /// </summary>
    internal static bool TryParseNestedSelect(string source, int start, out object? subquery, out int end, out ExpressionFailure? failure) =>
        Parser.ParseNestedSelect(source, start, armDepth: 1, out subquery, out end, out failure);

    private sealed class Parser(string s, int start = 0)
    {
        private int _p = start;

        public int Position => _p;

        /// <summary>The depth of parenthesized arms the position is inside: there a ')' ends a clause.</summary>
        private int _armDepth;

        internal static bool ParseNestedSelect(string source, int start, int armDepth, out object? subquery, out int end, out ExpressionFailure? failure)
        {
            subquery = null;
            end = start;
            failure = null;
            var subParser = new Parser(source, start) { _armDepth = armDepth };
            var inner = new SelectSyntax(source);
            SqlFailure? sf = subParser.QueryBody(inner);
            if (sf is null && subParser.BadName)
            {
                sf = SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
            }
            if (sf is not null)
            {
                inner.Dispose();
                failure = sf.IsNotModelled
                    ? ExpressionFailure.Open(ExpressionPhase.Parse, sf.NotModelled!, sf.Message) with { Site = subParser.Position }
                    : ExpressionFailure.Ace(ExpressionPhase.Parse, sf.Number, sf.Message) with { Site = subParser.Position };
                return false;
            }
            subquery = inner;
            end = subParser.Position;
            return true;
        }

        private bool ParseSubquery(string source, int start, out object? subquery, out int end, out ExpressionFailure? failure) =>
            ParseNestedSelect(source, start, _armDepth + 1, out subquery, out end, out failure);

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

        /// <summary>
        /// A name: <c>[any text]</c>, <c>`any text`</c> (qualified.md R-SQL-1 SQL-B, corrected: backticks quote names as
        /// brackets do, r-q3-sql/backtick-names) or a word that does not carry any role in <paramref name="disallowed"/>.
        /// </summary>
        private string? Name(WordRole disallowed = WordRole.Clause)
        {
            Skip();
            if (_p < s.Length && s[_p] is '[' or '`')
            {
                char closing = s[_p] == '[' ? ']' : '`';
                int close = s.IndexOf(closing, _p + 1);
                if (close < 0)
                {
                    return null;
                }
                string name = s[(_p + 1)..close];
                _p = close + 1;
                if (closing == ']' && _p < s.Length && s[_p] == ']')
                {
                    // r-sql-1/b-brackets: [x]]y] is 3141 (no escaped bracket).
                    BadName = true;
                }
                return name;
            }
            string? word = PeekWord();
            if (word is null || HasRole(word, disallowed))
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
            ExpressionSyntax syntax = ExpressionSyntax.ParseFragment(s, start, ParseSubquery, out int end);
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

        public SqlFailure? LeadingParameters(SelectSyntax statement)
        {
            if (TakeWord("PARAMETERS") && Parameters(statement) is { } declarations)
            {
                return declarations;
            }
            if (IsWord("PARAMETERS"))
            {
                return SqlFailure.Ace(SqlPhase.Parse, 3129, InvalidStatement);
            }
            return null;
        }

        /// <summary>
        /// A statement: <c>[PARAMETERS …;] arm {UNION [ALL] arm} [;]</c> (II-11 §2, §15). The last arm's ORDER BY is the
        /// union's; an ORDER BY on an earlier arm is not recorded. UNION is left-associative: each operator keeps its arm.
        /// </summary>
        public SqlFailure? Select(SelectSyntax statement, bool allowInto = false, bool allowWith = false)
        {
            if (LeadingParameters(statement) is { } declarations)
            {
                return declarations;
            }
            if (QueryBody(statement, allowInto, allowWith) is { } bodyFailure)
            {
                return bodyFailure;
            }
            if (allowWith && TakeWord("WITH"))
            {
                if (!TakeWord("OWNERACCESS") || !TakeWord("OPTION"))
                {
                    return Open("NotModelled.SqlSyntax", $"WITH clause at {_p} (ACE's answer not recorded)");
                }
                statement.WithOwnerAccess = true;
            }
            if (TakeSymbol(';'))
            {
                if (!AtEnd())
                {
                    // r-def-expr-sql/sql-text-framing-forms (#93, #150, #163), r-fix-wire-ado/ado-execute-update-compound (#29),
                    // r-str-ado-act/ado-execute-results (#42): tokens after the terminating semicolon fail 3142.
                    return SqlFailure.Ace(SqlPhase.Parse, 3142, SqlMessages.CharactersAfterEnd);
                }
                return null;
            }
            if (!AtEnd())
            {
                return Open("NotModelled.SqlSyntax", $"text after the statement at {_p} (ACE's answer not recorded)");
            }
            return null;
        }

        public SqlFailure? Crosstab(SelectSyntax statement, out Fragment? transform, out string? transformAlias, out Fragment? pivot, out bool hasPivotIn)
        {
            transform = null;
            transformAlias = null;
            pivot = null;
            hasPivotIn = false;
            if (LeadingParameters(statement) is { } declarations)
            {
                return declarations;
            }
            if (!TakeWord("TRANSFORM"))
            {
                return SqlFailure.Ace(SqlPhase.Parse, 3129, InvalidStatement);
            }
            if (Expression(statement, out transform) is { } transformFailure)
            {
                return transformFailure;
            }
            if (TakeWord("AS"))
            {
                transformAlias = Name(WordRole.ReservedAlias);
                if (transformAlias is null || BadName)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
                }
            }
            if (ArmBody(statement, allowNoFrom: false, allowWith: false, out _, allowInto: false, allowPivot: true) is { } bodyFailure)
            {
                return bodyFailure;
            }
            if (!TakeWord("PIVOT"))
            {
                return Open("NotModelled.CrosstabSyntax", "TRANSFORM without PIVOT (ACE's answer not recorded)");
            }
            if (Clause(statement, out pivot, "IN", "WITH") is { } pivotFailure)
            {
                return pivotFailure;
            }
            if (pivot!.Syntax.Root is { Kind: SyntaxKind.In, Negated: false } || TakeWord("IN"))
            {
                hasPivotIn = true;
                return null;
            }
            if (TakeWord("WITH"))
            {
                if (!TakeWord("OWNERACCESS") || !TakeWord("OPTION"))
                {
                    return Open("NotModelled.SqlSyntax", $"WITH clause at {_p} (ACE's answer not recorded)");
                }
                statement.WithOwnerAccess = true;
            }
            if (TakeSymbol(';'))
            {
                if (!AtEnd())
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3142, SqlMessages.CharactersAfterEnd);
                }
                return null;
            }
            if (!AtEnd())
            {
                return Open("NotModelled.SqlSyntax", $"text after the statement at {_p} (ACE's answer not recorded)");
            }
            return null;
        }

        private SqlFailure? QueryBody(SelectSyntax statement, bool allowInto = false, bool allowWith = false)
        {
            if (Arm(statement, allowInto, allowWith) is { } first)
            {
                return first;
            }
            SelectSyntax last = statement;
            while (TakeWord("UNION"))
            {
                bool all = TakeWord("ALL");
                if (IsWord("DISTINCT") || IsWord("CORRESPONDING"))
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3129, InvalidStatement);
                }
                if (last.OrderBy.Count > 0)
                {
                    return Open("NotModelled.ArmOrderBy", "an ORDER BY on an arm before UNION (ACE's answer not recorded)");
                }
                var arm = new SelectSyntax(s);
                statement.Unions.Add(new UnionArm(all, arm));
                if (Arm(arm, allowInto: false, allowWith) is { } armFailure)
                {
                    return armFailure;
                }
                last = arm;
            }
            if (statement.IsUnion)
            {
                statement.UnionOrderBy.AddRange(last.OrderBy);
                last.OrderBy.Clear();
            }
            return null;
        }

        /// <summary>
        /// <c>PARAMETERS name type [(n)], …;</c> (qualified.md R-SQL-3 SQL-META parameters; r-sql-13b/params-declaration-syntax):
        /// a missing ';', a missing type, an empty list, a trailing comma, <c>[p]] 2]</c> and the refused type names fail
        /// 3139. A name may be bracketed, dotted (<c>CX.x</c>) or a keyword (<c>[Date]</c>). A length is read for Text only.
        /// </summary>
        private SqlFailure? Parameters(SelectSyntax statement)
        {
            SqlFailure clause = SqlFailure.Ace(SqlPhase.Parse, 3139, ParameterClauseError);
            do
            {
                string? name = ParameterName();
                if (name is null || BadName)
                {
                    return clause;
                }
                string? typeWord = PeekWord();
                if (typeWord is null)
                {
                    return clause;
                }
                _p += typeWord.Length;
                if (!ParameterTypes.TryGetValue(typeWord, out int type))
                {
                    return Open("NotModelled.ParameterType", $"PARAMETERS type '{typeWord}' (not among the recorded names)");
                }
                if (type == 0)
                {
                    return clause;
                }
                int? textSize = null;
                if (TakeSymbol('('))
                {
                    Skip();
                    int digits = _p;
                    while (_p < s.Length && char.IsAsciiDigit(s[_p]))
                    {
                        _p++;
                    }
                    int digitsEnd = _p;
                    if (type != 10 || digitsEnd == digits || !TakeSymbol(')'))
                    {
                        return Open("NotModelled.ParameterLength", $"a length on a {typeWord} parameter (only Text ( n ) is recorded)");
                    }
                    if (!int.TryParse(s.AsSpan(digits, digitsEnd - digits), System.Globalization.CultureInfo.InvariantCulture, out int parsedSize))
                    {
                        return Open("NotModelled.ParameterLength", $"Text parameter length '{s[digits..digitsEnd]}' overflows Int32");
                    }
                    textSize = parsedSize;
                }
                statement.Parameters.Add(new ParameterDeclaration(name, type, typeWord.ToUpperInvariant() switch { "VALUE" => null, "BIGINT" => 16, "DATETIME2" => 26, _ => type }, textSize));
            }
            while (TakeSymbol(','));
            return TakeSymbol(';') ? null : clause;
        }

        /// <summary>
        /// A parameter's name as written, brackets kept (r-sql-13b/params-declaration-syntax: <c>[p 1]</c> is named "[p 1]"):
        /// a bracketed name, or words joined by dots, keywords included.
        /// </summary>
        private string? ParameterName()
        {
            Skip();
            int start = _p;
            if (_p < s.Length && s[_p] == '[')
            {
                return Name(WordRole.None) is null ? null : s[start.._p];
            }
            if (Name(WordRole.None) is null)
            {
                return null;
            }
            while (_p < s.Length && s[_p] == '.')
            {
                _p++;
                if (Name(WordRole.None) is null)
                {
                    return null;
                }
            }
            return s[start.._p];
        }

        /// <summary>One SELECT arm, bare or in parentheses (r-sql-13b/union-syntax-forms: <c>(SELECT a FROM JA) UNION (SELECT b FROM JB)</c>).</summary>
        private SqlFailure? Arm(SelectSyntax statement, bool allowInto = false, bool allowWith = false)
        {
            Skip();
            int save = _p;
            statement.ArmStart = save;
            if (TakeSymbol('(') && IsWord("SELECT"))
            {
                statement.ParenthesizedArm = true;
                _armDepth++;
                SqlFailure? inner = ArmBody(statement, allowNoFrom: false, allowWith, out _, allowInto, allowPivot: false);
                _armDepth--;
                if (inner is not null)
                {
                    return inner;
                }
                if (!TakeSymbol(')'))
                {
                    return Open("NotModelled.SqlSyntax", $"an unclosed parenthesized SELECT at {_p} (ACE's answer not recorded)");
                }
                statement.ArmEnd = _p;
                return null;
            }
            _p = save;
            SqlFailure? result = ArmBody(statement, allowNoFrom: false, allowWith, out _, allowInto, allowPivot: false);
            statement.ArmEnd = _p;
            return result;
        }

        public SqlFailure? InsertSelectBody(SelectSyntax statement, out bool hasFrom)
        {
            SqlFailure? f = ArmBody(statement, allowNoFrom: true, allowWith: true, out hasFrom, allowInto: false, allowPivot: false);
            if (f is not null)
            {
                return f;
            }
            if (IsWord("UNION"))
            {
                // r-def-expr-sql/sql-insert-values-forms: INSERT INTO U ... SELECT ... UNION SELECT ... fails 3131.
                return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
            }
            return null;
        }

        private SqlFailure? ArmBody(SelectSyntax statement) => ArmBody(statement, allowNoFrom: false, allowWith: false, out _, allowInto: false, allowPivot: false);

        private SqlFailure? ArmBody(SelectSyntax statement, bool allowNoFrom, bool allowWith, out bool hasFrom, bool allowInto = false, bool allowPivot = false)
        {
            hasFrom = false;
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
                if (Item(statement, allowWith, allowInto) is { } itemFailure)
                {
                    return itemFailure;
                }
            }
            while (TakeSymbol(','));
            if (allowInto && TakeWord("INTO"))
            {
                string? target = Name(WordRole.Clause);
                if (target is null || BadName)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
                }
                if (TakeWord("IN"))
                {
                    return Open("NotModelled.ExternalDatabase", "SELECT INTO ... IN 'file' (external target not recorded)");
                }
                statement.IntoTarget = target;
            }
            if (!TakeWord("FROM"))
            {
                if (allowNoFrom && (AtEnd() || IsSymbol(';') || (allowWith && IsWord("WITH"))))
                {
                    return null;
                }
                if (_armDepth == 0 && IsSymbol(';'))
                {
                    int saved = _p;
                    TakeSymbol(';');
                    if (!AtEnd())
                    {
                        // r-fix-wire-ado/ado-execute-update-compound (#29), r-str-ado-act/ado-execute-results (#42):
                        // "SELECT 1 AS A; SELECT 2 AS B" -> 3142.
                        return SqlFailure.Ace(SqlPhase.Parse, 3142, SqlMessages.CharactersAfterEnd);
                    }
                    _p = saved;
                }
                // r-q52-ddl-make-domain/domain-all-scalar-dao-* (SELECT DCount(...), DSum(...), DLookup(...)),
                // r-def-expr-sql/sql-text-framing-forms (SELECT 1, SELECT 1 AS v, SELECT 1 AS v;):
                // plain FROM-less SELECT with no *, DISTINCT/DISTINCTROW, or TOP produces 1 row.
                bool plainNoFrom = statement.Predicate == SelectPredicate.All
                    && statement.Top is null
                    && statement.IntoTarget is null
                    && !statement.Items.Any(static i => i.Star);
                if (plainNoFrom && (AtEnd() || (_armDepth == 0 && IsSymbol(';')) || (_armDepth > 0 && IsSymbol(')'))))
                {
                    return null;
                }
                if (AtEnd() || (_armDepth == 0 && IsSymbol(';')) || (_armDepth > 0 && IsSymbol(')')))
                {
                    return Open("NotModelled.NoFrom", "a SELECT without FROM");
                }
                return MissingOperator(statement.Items[^1].Offset);
            }
            hasFrom = true;
            do
            {
                if (Source(statement, out SourceSyntax? source) is { } sourceFailure)
                {
                    return sourceFailure;
                }
                statement.From.Add(source!);
            }
            while (TakeSymbol(','));
            if (IsWord("SELECT"))
            {
                // r-sql-13b/union-syntax-forms: INTERSECT, EXCEPT and MINUS are read as an alias, then SELECT is 3131.
                return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
            }
            if (TakeWord("WHERE"))
            {
                string[] whereFollowers = allowPivot ? ["GROUP", "HAVING", "ORDER", "PIVOT"] : allowWith ? ["GROUP", "HAVING", "ORDER", "WITH"] : ["GROUP", "HAVING", "ORDER"];
                if (Clause(statement, out Fragment? where, whereFollowers) is { } f)
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
                string[] groupFollowers = allowPivot ? ["HAVING", "ORDER", "PIVOT"] : allowWith ? ["HAVING", "ORDER", "WITH"] : ["HAVING", "ORDER"];
                do
                {
                    if (Clause(statement, out Fragment? key, groupFollowers) is { } f)
                    {
                        return f;
                    }
                    statement.GroupBy.Add(key!);
                }
                while (TakeSymbol(','));
            }
            if (TakeWord("HAVING"))
            {
                string[] havingFollowers = allowPivot ? ["ORDER", "PIVOT"] : allowWith ? ["ORDER", "WITH"] : ["ORDER"];
                if (Clause(statement, out Fragment? having, havingFollowers) is { } f)
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
                string[] orderFollowers = allowPivot ? ["ASC", "DESC", "PIVOT"] : allowWith ? ["ASC", "DESC", "WITH"] : ["ASC", "DESC"];
                do
                {
                    if (Clause(statement, out Fragment? key, orderFollowers) is { } f)
                    {
                        return f;
                    }
                    if (FindSubquery(key!.Syntax.Root!) is { } orderSub)
                    {
                        // r-sql-13b/order-expression-forms #643, sub-correlated-select-list #695:
                        // a subquery in ORDER BY fails 3075 with quote ending before the closing ')'.
                        int stop = orderSub.CloseOffset > key.Start && orderSub.CloseOffset <= s.Length ? orderSub.CloseOffset : key.End;
                        string quote = s[key.Start..Math.Max(key.Start, stop)].TrimEnd();
                        return new SqlFailure(SqlPhase.Parse, 3075, $"Syntax error{SqlFailure.QueryExpression}{quote}'.") { ExcerptExact = false };
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
            if (!AtEnd() && !IsSymbol(';') && !IsWord("UNION") && !(allowWith && IsWord("WITH")) && !(allowPivot && IsWord("PIVOT")) && !(_armDepth > 0 && IsSymbol(')')))
            {
                return Open("NotModelled.SqlSyntax", $"text after the statement at {_p} (ACE's answer not recorded)");
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

        private SqlFailure? Item(SelectSyntax statement, bool allowWith = false, bool allowInto = false)
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
                return HasInvalidTopLevelAlias(offset, allowWith)
                    ? SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError)
                    : f;
            }
            string? alias = null;
            if (TakeWord("AS"))
            {
                alias = Name(WordRole.ReservedAlias);
                if (alias is null || BadName)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3141, SelectSyntaxError);
                }
            }
            statement.Items.Add(new SelectItem(expression, false, null, alias, offset));
            if (!IsSymbol(',') && !IsWord("FROM") && !(allowInto && IsWord("INTO")) && !AtEnd() && !(_armDepth == 0 && IsSymbol(';')) && !(_armDepth > 0 && IsSymbol(')')) && !(allowWith && (IsSymbol(';') || IsWord("WITH"))))
            {
                return MissingOperator(offset);
            }
            return null;
        }

        /// <summary>
        /// When an item's expression fails to parse (3075), scan the current SELECT item at parenthesis depth 0 for a
        /// top-level <c>AS</c> followed by a reserved word or an unclosed bracketed/backticked name
        /// (r-q25-nz-3141/alias-int-precedence #20; r-q25-nz-3141/alias-precedence-forms #8, #11, #14, #17, #23, #26, #35, #38:
        /// <c>AS inT</c>, <c>AS FROM</c>, unclosed <c>AS [inT</c>, and <c>AS OUTER</c> fail 3141, while <c>AS 5</c>,
        /// <c>AS [x]]y]</c>, <c>CAST(1 AS LONG) AS v</c>, and a later item's <c>AS inT</c> keep the expression's own failure).
        /// </summary>
        private bool HasInvalidTopLevelAlias(int start, bool allowWith)
        {
            int savedP = _p;
            bool savedBad = BadName;
            try
            {
                _p = start;
                int depth = 0;
                char prev = '\0';
                while (!AtEnd())
                {
                    char c = s[_p];
                    if (c is '\'' or '"')
                    {
                        _p++;
                        while (_p < s.Length)
                        {
                            if (s[_p++] == c)
                            {
                                if (_p < s.Length && s[_p] == c)
                                {
                                    _p++;
                                    continue;
                                }
                                break;
                            }
                        }
                        prev = c;
                        continue;
                    }
                    if (c == '#')
                    {
                        int close = s.IndexOf('#', _p + 1);
                        if (close < 0)
                        {
                            return false;
                        }
                        _p = close + 1;
                        prev = '#';
                        continue;
                    }
                    if (c is '[' or '`')
                    {
                        if (Name(WordRole.None) is null)
                        {
                            return false;
                        }
                        prev = c;
                        continue;
                    }
                    if (c == '(')
                    {
                        depth++;
                        _p++;
                        prev = '(';
                        continue;
                    }
                    if (c == ')')
                    {
                        if (depth > 0)
                        {
                            depth--;
                        }
                        else if (_armDepth > 0)
                        {
                            return false;
                        }
                        _p++;
                        prev = ')';
                        continue;
                    }
                    if (depth == 0 && c is ',' or ';')
                    {
                        return false;
                    }
                    if (PeekWord() is { } word)
                    {
                        _p += word.Length;
                        if (depth == 0 && prev is not ('.' or '!'))
                        {
                            if (word.Equals("FROM", StringComparison.OrdinalIgnoreCase)
                                || (allowWith && word.Equals("WITH", StringComparison.OrdinalIgnoreCase)))
                            {
                                return false;
                            }
                            if (word.Equals("AS", StringComparison.OrdinalIgnoreCase))
                            {
                                Skip();
                                if (_p < s.Length && s[_p] is '[' or '`')
                                {
                                    return Name(WordRole.None) is null;
                                }
                                return PeekWord() is { } alias && HasRole(alias, WordRole.ReservedAlias);
                            }
                        }
                        prev = 'A';
                        continue;
                    }
                    prev = c;
                    _p++;
                }
                return false;
            }
            finally
            {
                _p = savedP;
                BadName = savedBad;
            }
        }

        private static SyntaxNode? FindSubquery(SyntaxNode node)
        {
            if (node.Kind is SyntaxKind.Subquery or SyntaxKind.Exists or SyntaxKind.InSubquery or SyntaxKind.QuantifiedComparison)
            {
                return node;
            }
            if (node.Left is { } l && FindSubquery(l) is { } fl)
            {
                return fl;
            }
            if (node.Right is { } r && FindSubquery(r) is { } fr)
            {
                return fr;
            }
            for (int i = 0; i < node.Items.Count; i++)
            {
                if (FindSubquery(node.Items[i]) is { } fi)
                {
                    return fi;
                }
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
            if (AtEnd() || IsSymbol(',') || IsSymbol(';') || IsWord("UNION") || (_armDepth > 0 && IsSymbol(')')) || followers.Any(IsWord))
            {
                return null;
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
                    return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
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
                    var inner = new SelectSyntax(s);
                    _armDepth++;
                    SqlFailure? subFailure = QueryBody(inner);
                    _armDepth--;
                    if (subFailure is not null)
                    {
                        inner.Dispose();
                        return subFailure;
                    }
                    if (!TakeSymbol(')'))
                    {
                        inner.Dispose();
                        return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
                    }
                    if (SourceAlias(out string? subAlias) is { } aliasFailure)
                    {
                        inner.Dispose();
                        return aliasFailure;
                    }
                    source = new SubquerySource(inner, subAlias, offset);
                    return null;
                }
                if (Source(statement, out source) is { } f)
                {
                    return f;
                }
                return TakeSymbol(')') ? null : SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
            }
            string? name = Name();
            if (name is null)
            {
                return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
            }
            if (IsWord("IN"))
            {
                return Open("NotModelled.ExternalSource", "FROM … IN 'file' (II-11 §2 external IN)");
            }
            if (SourceAlias(out string? alias) is { } namedAliasFailure)
            {
                return namedAliasFailure;
            }
            source = new NamedSource(name, alias, offset);
            return null;
        }

        private SqlFailure? SourceAlias(out string? alias)
        {
            alias = null;
            if (TakeWord("AS"))
            {
                if (IsWord("UNION"))
                {
                    // r-q25-nz-3141/alias-from-words #107: FROM Mx AS UNION fails 3450 (the 65 other reserved words fail 3131).
                    return SqlFailure.Ace(SqlPhase.Parse, 3450, IncompleteQueryClause);
                }
                alias = Name(WordRole.ReservedAlias);
                if (alias is null)
                {
                    return SqlFailure.Ace(SqlPhase.Parse, 3131, FromSyntaxError);
                }
            }
            else if (Name() is { } bare)
            {
                alias = bare;
            }
            return null;
        }
    }
}