using CalcDb.Expressions;

namespace CalcDb.Sql;

/// <summary>The single-table action statement forms covered by W-07/actions stretch 1 (II-11 §§22–28).</summary>
public enum ActionKind : byte
{
    InsertValues,
    InsertSelect,
    Update,
    Delete,
}

/// <summary>One <c>[table.]column = expression</c> assignment in an <c>UPDATE … SET</c> clause (II-11 §24).</summary>
public sealed record SetAssignment(string? Qualifier, string Column, Fragment Value, int Offset);

/// <summary>
/// A parsed single-table action statement (W-07/actions; II-11 §§22–28): <c>INSERT INTO … VALUES (…)</c>,
/// <c>INSERT INTO … SELECT …</c>, <c>UPDATE … SET … [WHERE …]</c>, or <c>DELETE […] FROM … [WHERE …]</c>, with optional
/// leading <c>PARAMETERS …;</c> and trailing <c>WITH OWNERACCESS OPTION</c>. Owns its expression fragments and any
/// source <see cref="SelectSyntax"/> until <see cref="Dispose"/>.
/// </summary>
public sealed class ActionSyntax : IDisposable
{
    internal ActionSyntax(string source, ActionKind kind, string targetName, string? targetAlias)
    {
        Source = source;
        Kind = kind;
        TargetName = targetName;
        TargetAlias = targetAlias;
    }

    public string Source { get; }

    public ActionKind Kind { get; }

    /// <summary>The leading PARAMETERS declarations, in declaration order.</summary>
    public List<ParameterDeclaration> Parameters { get; } = [];

    /// <summary>The target table name as written (brackets/backticks stripped).</summary>
    public string TargetName { get; }

    /// <summary>The target alias when written (<c>UPDATE T AS d</c>, <c>DELETE FROM T AS d</c>), else null.</summary>
    public string? TargetAlias { get; }

    /// <summary>Whether <c>WITH OWNERACCESS OPTION</c> was present (accepted as no-op under Admin: R-SQL-2 SQL-OWNERACCESS).</summary>
    public bool WithOwnerAccess { get; internal set; }

    /// <summary>Explicit target column list of <c>INSERT INTO T (c1, …)</c>, or null when omitted.</summary>
    public List<string>? TargetColumns { get; internal set; }

    /// <summary>The value expressions of <c>INSERT INTO … VALUES (e1, …)</c>.</summary>
    public List<Fragment> Values { get; } = [];

    /// <summary>The source SELECT of <c>INSERT INTO … SELECT …</c> (both with FROM and FROM-less constant SELECT).</summary>
    public SelectSyntax? SourceSelect { get; internal set; }

    /// <summary>Whether <see cref="SourceSelect"/> has a FROM clause (false for <c>INSERT INTO U (ID, K) SELECT 602, 'y'</c>).</summary>
    public bool SourceSelectHasFrom { get; internal set; }

    /// <summary>The SET assignments of <c>UPDATE</c>, in textual order.</summary>
    public List<SetAssignment> Assignments { get; } = [];

    /// <summary>The WHERE predicate of <c>UPDATE</c> or <c>DELETE</c>, or null when absent.</summary>
    public Fragment? Where { get; internal set; }

    internal List<Fragment> Fragments { get; } = [];

    public void Dispose()
    {
        foreach (Fragment f in Fragments)
        {
            f.Syntax.Dispose();
        }
        Fragments.Clear();
        SourceSelect?.Dispose();
        SourceSelect = null;
    }
}

/// <summary>
/// Access SQL action statement parser (W-07/actions; II-11 §§22–28): parses single-table <c>INSERT INTO … VALUES</c>,
/// <c>INSERT INTO … SELECT</c>, <c>UPDATE … SET … [WHERE …]</c>, and <c>DELETE […] FROM … [WHERE …]</c> over the one
/// expression grammar (<see cref="ExpressionSyntax.ParseFragment"/>) and the one <see cref="SqlParser"/> SELECT and
/// PARAMETERS grammar. Multi-table/joined actions, <c>SELECT INTO</c>, external <c>IN 'file'</c> and DDL statements are
/// refused with explicit named <c>NotModelled.*</c> outcomes; recorded ACE syntax errors cite their retained records.
/// </summary>
public static class ActionParser
{
    private static readonly HashSet<string> TargetStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SET", "WHERE", "VALUES", "SELECT", "INTO", "FROM", "INNER", "LEFT", "RIGHT", "OUTER", "JOIN", "ON", "IN", "WITH", "ORDER", "GROUP", "HAVING", "LIMIT",
    };

    /// <summary>
    /// Leading SQL statement words that <c>Database.Execute</c> routes to the SQL parser rather than treating as a saved
    /// query name (r-def-expr-sql/sql-unsupported-statements: BEGIN/EXEC/EXECUTE enter the SQL parser and fail 3129,
    /// whereas TRUNCATE/MERGE/REPLACE/RENAME/USE/SET/COMMIT/ROLLBACK/SAVEPOINT/EXPLAIN/SHOW/DESCRIBE/PRAGMA/CALL/
    /// ANALYZE/VACUUM/LOCK/DECLARE/PRINT/COMMENT and misspelled SELEC in r-err-1b/sql-syntax-selec are looked up as
    /// saved query names and fail 3078).
    /// </summary>
    private static readonly HashSet<string> SqlDispatchWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "PARAMETERS", "TRANSFORM", "PROCEDURE",
        "CREATE", "ALTER", "DROP", "GRANT", "REVOKE", "BEGIN", "EXEC", "EXECUTE",
    };

    /// <summary>Whether <paramref name="source"/> starts with a SQL action keyword (<c>INSERT</c>, <c>UPDATE</c>, or <c>DELETE</c>), optionally after <c>PARAMETERS …;</c>.</summary>
    public static bool IsAction(string source)
    {
        int nul = source.IndexOf('\0', StringComparison.Ordinal);
        ReadOnlySpan<char> text = (nul >= 0 ? source.AsSpan(0, nul) : source.AsSpan()).TrimStart();
        if (StartsWithWord(text, "PARAMETERS"))
        {
            int semi = text.IndexOf(';');
            if (semi < 0)
            {
                return false;
            }
            text = text[(semi + 1)..].TrimStart();
        }
        return StartsWithWord(text, "INSERT") || StartsWithWord(text, "UPDATE") || StartsWithWord(text, "DELETE");
    }

    /// <summary>Whether <c>Database.Execute</c> parses <paramref name="source"/> as SQL rather than looking it up as a saved query name.</summary>
    public static bool IsSqlStatement(string source)
    {
        int nul = source.IndexOf('\0', StringComparison.Ordinal);
        ReadOnlySpan<char> text = (nul >= 0 ? source.AsSpan(0, nul) : source.AsSpan()).TrimStart();
        if (text.Length == 0)
        {
            return false;
        }
        if (text[0] == '(')
        {
            return true;
        }
        int end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }
        return end > 0 && SqlDispatchWords.Contains(text[..end].ToString());
    }

    private static bool StartsWithWord(ReadOnlySpan<char> text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (text.Length == word.Length || !(char.IsLetterOrDigit(text[word.Length]) || text[word.Length] == '_'));

    /// <summary>
    /// Parse an executable SQL statement (<c>Database.Execute</c> / <c>QueryDef.Execute</c>). Returns null with ACE's
    /// error or a named <c>NotModelled.*</c> failure when the statement is not a single-table action in scope.
    /// </summary>
    public static ActionSyntax? Parse(string source, out SqlFailure? failure)
    {
        if (!SqlParser.NormalizeSource(ref source, out failure))
        {
            return null;
        }
        var parameters = new List<ParameterDeclaration>();
        int pos = 0;
        if (SqlParser.ParseLeadingParameters(source, ref pos, parameters) is { } paramFailure)
        {
            failure = paramFailure;
            return null;
        }
        var p = new Parser(source, pos, parameters);
        ActionSyntax? action = p.Statement(out failure);
        if (failure is not null)
        {
            action?.Dispose();
            return null;
        }
        return action;
    }

    private sealed class Parser(string s, int start, List<ParameterDeclaration> parameters)
    {
        private int _p = start;

        private static SqlFailure Open(string name, string detail) => SqlFailure.Open(SqlPhase.Parse, name, detail);

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

        private string? Name(bool allowStopWord = false)
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
                return name;
            }
            string? word = PeekWord();
            if (word is null || (!allowStopWord && TargetStopWords.Contains(word)))
            {
                return null;
            }
            _p += word.Length;
            return word;
        }

        private SqlFailure? Expression(ActionSyntax action, out Fragment? fragment)
        {
            Skip();
            int exprStart = _p;
            ExpressionSyntax syntax = ExpressionSyntax.ParseFragment(s, exprStart, out int end);
            if (syntax.Failure is { } f)
            {
                fragment = null;
                return SqlFailure.Of(f, SqlPhase.Parse, s, exprStart, end);
            }
            fragment = new Fragment(syntax, exprStart, end);
            action.Fragments.Add(fragment);
            _p = end;
            return null;
        }

        private SqlFailure MissingOperator(int exprStart)
        {
            Skip();
            int stop = _p;
            while (stop < s.Length && !char.IsWhiteSpace(s[stop]) && s[stop] != ';')
            {
                stop++;
            }
            return new SqlFailure(SqlPhase.Parse, 3075, $"Syntax error (missing operator){SqlFailure.QueryExpression}{s[exprStart..stop]}'.") { ExcerptExact = false };
        }

        private bool PeekStartsSubquery()
        {
            int save = _p;
            bool hit = TakeSymbol('(') && IsWord("SELECT");
            _p = save;
            return hit;
        }

        public ActionSyntax? Statement(out SqlFailure? failure)
        {
            string? lead = PeekWord();
            if (lead is null)
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3129, SqlParser.InvalidStatement);
                return null;
            }
            if (TakeWord("INSERT"))
            {
                return Insert(out failure);
            }
            if (TakeWord("UPDATE"))
            {
                return Update(out failure);
            }
            if (TakeWord("DELETE"))
            {
                return Delete(out failure);
            }
            if (IsWord("SELECT"))
            {
                if (HasTopLevelIntoBeforeFrom())
                {
                    failure = Open("NotModelled.MakeTableAction", "SELECT … INTO … (make-table action query is outside W-07/actions stretch 1)");
                    return null;
                }
                if (parameters.Count > 0)
                {
                    failure = Open("NotModelled.ExecuteParameterSelect", "Database.Execute of a SELECT with a PARAMETERS clause: 3061 or 3065 is not recorded");
                    return null;
                }
                // r-err-1b/sql-execute-select, r-sql-2-3/act-records-affected: Execute of a SELECT is 3065.
                failure = SqlFailure.Ace(SqlPhase.Parse, 3065, SqlMessages.CannotExecuteSelect);
                return null;
            }
            if (lead.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
                || lead.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
                || lead.Equals("DROP", StringComparison.OrdinalIgnoreCase)
                || lead.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
                || lead.Equals("REVOKE", StringComparison.OrdinalIgnoreCase))
            {
                failure = Open("NotModelled.DdlStatement", $"DDL statement starting '{lead}' (outside W-07/actions single-table DML)");
                return null;
            }
            // r-def-cursor-dao/errors-refresh-and-members, r-fix-cursor-dao/proto-two-fails-different-numbers: SELEC -> 3129.
            failure = SqlFailure.Ace(SqlPhase.Parse, 3129, SqlParser.InvalidStatement);
            return null;
        }

        private bool HasTopLevelIntoBeforeFrom()
        {
            int depth = 0;
            char quote = '\0';
            int i = _p;
            while (i < s.Length)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    i++;
                    continue;
                }
                if (c is '\'' or '"')
                {
                    quote = c;
                    i++;
                    continue;
                }
                if (c == '[')
                {
                    int close = s.IndexOf(']', i + 1);
                    i = close < 0 ? s.Length : close + 1;
                    continue;
                }
                if (c == '(')
                {
                    depth++;
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    if (depth > 0)
                    {
                        depth--;
                    }
                    i++;
                    continue;
                }
                if (depth == 0 && (char.IsLetter(c) || c == '_'))
                {
                    int q = i;
                    while (q < s.Length && (char.IsLetterOrDigit(s[q]) || s[q] == '_'))
                    {
                        q++;
                    }
                    ReadOnlySpan<char> w = s.AsSpan(i, q - i);
                    if (w.Equals("INTO", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    if (w.Equals("FROM", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    i = q;
                    continue;
                }
                i++;
            }
            return false;
        }

        /// <summary>
        /// <c>INSERT INTO target [(c1, …)] VALUES (e1, …)</c> or <c>INSERT INTO target [(c1, …)] SELECT …</c>
        /// (r-def-expr-sql/sql-insert-values-forms, r-sql-2-3/act-ins-mapping, act-ins-defaults, act-ins-self).
        /// </summary>
        private ActionSyntax? Insert(out SqlFailure? failure)
        {
            if (!TakeWord("INTO"))
            {
                // r-def-expr-sql/sql-insert-values-forms: INSERT U (...) VALUES (...) -> 3134.
                failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                return null;
            }
            string? target = Name();
            if (target is null)
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                return null;
            }
            if (IsWord("IN"))
            {
                failure = Open("NotModelled.ExternalDatabaseAction", "INSERT INTO … IN 'file' (external database action)");
                return null;
            }
            List<string>? targetCols = null;
            if (TakeSymbol('('))
            {
                targetCols = [];
                do
                {
                    string? col = Name(allowStopWord: true);
                    if (col is null)
                    {
                        failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                        return null;
                    }
                    targetCols.Add(col);
                }
                while (TakeSymbol(','));
                if (!TakeSymbol(')'))
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                    return null;
                }
            }
            if (IsWord("IN"))
            {
                failure = Open("NotModelled.ExternalDatabaseAction", "INSERT INTO … IN 'file' (external database action)");
                return null;
            }
            if (TakeWord("VALUES"))
            {
                var action = new ActionSyntax(s, ActionKind.InsertValues, target, null) { TargetColumns = targetCols };
                action.Parameters.AddRange(parameters);
                if (!TakeSymbol('('))
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                    action.Dispose();
                    return null;
                }
                if (IsSymbol(')'))
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                    action.Dispose();
                    return null;
                }
                do
                {
                    if (PeekStartsSubquery())
                    {
                        // r-def-expr-sql/sql-insert-values-forms: INSERT INTO U (ID, K) VALUES (14, (SELECT ...)) -> 3067.
                        failure = SqlFailure.Ace(SqlPhase.Parse, 3067, SqlMessages.MustContainTable);
                        action.Dispose();
                        return null;
                    }
                    if (Expression(action, out Fragment? value) is { } vf)
                    {
                        failure = vf;
                        action.Dispose();
                        return null;
                    }
                    action.Values.Add(value!);
                }
                while (TakeSymbol(','));
                if (!TakeSymbol(')'))
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
                    action.Dispose();
                    return null;
                }
                if (FinishAction(action) is { } endFail)
                {
                    failure = endFail;
                    action.Dispose();
                    return null;
                }
                failure = null;
                return action;
            }
            if (IsWord("SELECT"))
            {
                int selectPos = _p;
                SelectSyntax? select = SqlParser.ParseInsertSelect(s, ref selectPos, out bool hasFrom, out failure);
                if (select is null)
                {
                    return null;
                }
                _p = selectPos;
                var action = new ActionSyntax(s, ActionKind.InsertSelect, target, null)
                {
                    TargetColumns = targetCols,
                    SourceSelect = select,
                    SourceSelectHasFrom = hasFrom,
                };
                action.Parameters.AddRange(parameters);
                if (FinishAction(action) is { } endFail)
                {
                    failure = endFail;
                    action.Dispose();
                    return null;
                }
                failure = null;
                return action;
            }
            failure = SqlFailure.Ace(SqlPhase.Parse, 3134, SqlMessages.InsertSyntaxError);
            return null;
        }

        /// <summary>
        /// <c>UPDATE target [[AS] alias] SET [qual.]col = expr, … [WHERE expr] [WITH OWNERACCESS OPTION] [;]</c>
        /// (r-sql-2-3/act-upd-assign, r-def-expr-sql/sql-update-assignment-order).
        /// </summary>
        private ActionSyntax? Update(out SqlFailure? failure)
        {
            if (IsSymbol('('))
            {
                failure = Open("NotModelled.JoinedAction", "parenthesized joined UPDATE target (single-table actions only in stretch 1)");
                return null;
            }
            string? target = Name();
            if (target is null)
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                return null;
            }
            if (IsWord("IN"))
            {
                failure = Open("NotModelled.ExternalDatabaseAction", "UPDATE … IN 'file' (external database action)");
                return null;
            }
            string? alias = null;
            if (TakeWord("AS"))
            {
                alias = Name(allowStopWord: true);
                if (alias is null)
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                    return null;
                }
            }
            else if (!IsWord("SET") && !IsSymbol(',') && !IsJoinStart())
            {
                alias = Name();
            }
            if (IsSymbol(',') || IsJoinStart())
            {
                failure = Open("NotModelled.JoinedAction", "multi-table or joined UPDATE (single-table actions only in stretch 1)");
                return null;
            }
            if (!TakeWord("SET"))
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                return null;
            }
            var action = new ActionSyntax(s, ActionKind.Update, target, alias);
            action.Parameters.AddRange(parameters);
            do
            {
                Skip();
                int assignOffset = _p;
                if (IsSymbol('('))
                {
                    // r-def-expr-sql/sql-update-assignment-order: UPDATE Dm SET (N, V) = (1, 2) -> 3144.
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                    action.Dispose();
                    return null;
                }
                string? first = Name(allowStopWord: true);
                if (first is null)
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                    action.Dispose();
                    return null;
                }
                string? qualifier = null;
                string column = first;
                if (TakeSymbol('.'))
                {
                    string? second = Name(allowStopWord: true);
                    if (second is null)
                    {
                        failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                        action.Dispose();
                        return null;
                    }
                    qualifier = first;
                    column = second;
                }
                if (!TakeSymbol('='))
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3144, SqlMessages.UpdateSyntaxError);
                    action.Dispose();
                    return null;
                }
                if (PeekStartsSubquery())
                {
                    // r-def-expr-sql/sql-update-assignment-order: UPDATE Dm SET N = (SELECT ...) -> 3073.
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3073, SqlMessages.NotUpdateable);
                    action.Dispose();
                    return null;
                }
                Skip();
                int rhsStart = _p;
                if (Expression(action, out Fragment? rhs) is { } rf)
                {
                    failure = rf;
                    action.Dispose();
                    return null;
                }
                if (!IsSymbol(',') && !IsWord("WHERE") && !IsWord("WITH") && !IsSymbol(';') && !AtEnd())
                {
                    // r-def-expr-sql/sql-update-assignment-order: UPDATE Dm SET N = 1 FROM One -> 3075 missing operator.
                    failure = MissingOperator(rhsStart);
                    action.Dispose();
                    return null;
                }
                action.Assignments.Add(new SetAssignment(qualifier, column, rhs!, assignOffset));
            }
            while (TakeSymbol(','));
            if (TakeWord("WHERE"))
            {
                if (ParseWhere(action) is { } wf)
                {
                    failure = wf;
                    action.Dispose();
                    return null;
                }
            }
            if (FinishAction(action) is { } endFail)
            {
                failure = endFail;
                action.Dispose();
                return null;
            }
            failure = null;
            return action;
        }

        /// <summary>
        /// <c>DELETE [DISTINCTROW] [* | target.* | col] FROM target [[AS] alias] [WHERE expr] [WITH OWNERACCESS OPTION] [;]</c>
        /// (r-sql-2-3/act-delete, r-def-expr-sql/sql-delete-forms).
        /// </summary>
        private ActionSyntax? Delete(out SqlFailure? failure)
        {
            if (IsWord("TOP"))
            {
                // r-def-expr-sql/sql-delete-forms: DELETE TOP 1 FROM Dm -> 3130.
                failure = SqlFailure.Ace(SqlPhase.Parse, 3130, SqlMessages.DeleteSyntaxError);
                return null;
            }
            TakeWord("DISTINCTROW");
            bool hasTargetSpec = false;
            if (!IsWord("FROM"))
            {
                // Accept '*', 'target.*', or a single column name before FROM (r-def-expr-sql/sql-delete-forms:
                // DELETE *, DELETE Dm.*, DELETE d.*, DELETE ID FROM Dm all succeed on a single table).
                hasTargetSpec = true;
                if (TakeSymbol('*'))
                {
                    // ok
                }
                else
                {
                    string? spec = Name(allowStopWord: true);
                    if (spec is null)
                    {
                        failure = SqlFailure.Ace(SqlPhase.Parse, 3130, SqlMessages.DeleteSyntaxError);
                        return null;
                    }
                    if (TakeSymbol('.'))
                    {
                        if (!TakeSymbol('*') && Name(allowStopWord: true) is null)
                        {
                            failure = SqlFailure.Ace(SqlPhase.Parse, 3130, SqlMessages.DeleteSyntaxError);
                            return null;
                        }
                    }
                }
            }
            if (!TakeWord("FROM"))
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3130, SqlMessages.DeleteSyntaxError);
                return null;
            }
            if (IsSymbol('('))
            {
                failure = Open("NotModelled.JoinedAction", "parenthesized joined DELETE source (single-table actions only in stretch 1)");
                return null;
            }
            string? target = Name();
            if (target is null)
            {
                failure = SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
                return null;
            }
            if (IsWord("IN"))
            {
                failure = Open("NotModelled.ExternalDatabaseAction", "DELETE FROM … IN 'file' (external database action)");
                return null;
            }
            string? alias = null;
            if (TakeWord("AS"))
            {
                alias = Name(allowStopWord: true);
                if (alias is null)
                {
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
                    return null;
                }
            }
            else if (!IsWord("WHERE") && !IsWord("WITH") && !IsWord("ORDER") && !IsWord("LIMIT") && !IsSymbol(',') && !IsSymbol(';') && !IsJoinStart() && !AtEnd())
            {
                alias = Name();
            }
            if (IsWord("LIMIT"))
            {
                // r-def-expr-sql/sql-delete-forms: DELETE FROM Dm LIMIT 1 -> 3131.
                failure = SqlFailure.Ace(SqlPhase.Parse, 3131, "Syntax error in FROM clause.");
                return null;
            }
            if (IsSymbol(','))
            {
                if (!hasTargetSpec)
                {
                    // r-def-expr-sql/sql-delete-forms: DELETE FROM Dm, One -> 3128.
                    failure = SqlFailure.Ace(SqlPhase.Parse, 3128, SqlMessages.SpecifyDeleteTable);
                    return null;
                }
                failure = Open("NotModelled.JoinedAction", "multi-table DELETE (single-table actions only in stretch 1)");
                return null;
            }
            if (IsJoinStart())
            {
                failure = Open("NotModelled.JoinedAction", "joined DELETE (single-table actions only in stretch 1)");
                return null;
            }
            var action = new ActionSyntax(s, ActionKind.Delete, target, alias);
            action.Parameters.AddRange(parameters);
            if (TakeWord("WHERE"))
            {
                if (ParseWhere(action) is { } wf)
                {
                    failure = wf;
                    action.Dispose();
                    return null;
                }
            }
            if (FinishAction(action) is { } endFail)
            {
                failure = endFail;
                action.Dispose();
                return null;
            }
            failure = null;
            return action;
        }

        private SqlFailure? ParseWhere(ActionSyntax action)
        {
            Skip();
            int whereStart = _p;
            if (ContainsSubqueryKeyword(whereStart))
            {
                return Open("NotModelled.Subquery", "subquery in action WHERE clause (outside W-07/actions stretch 1)");
            }
            if (Expression(action, out Fragment? where) is { } wf)
            {
                return wf;
            }
            if (!AtEnd() && !IsSymbol(';') && !IsWord("WITH") && !IsWord("ORDER"))
            {
                return MissingOperator(whereStart);
            }
            action.Where = where;
            return null;
        }

        private bool ContainsSubqueryKeyword(int start)
        {
            char quote = '\0';
            int i = start;
            while (i < s.Length)
            {
                char c = s[i];
                if (quote != '\0')
                {
                    if (c == quote)
                    {
                        quote = '\0';
                    }
                    i++;
                    continue;
                }
                if (c is '\'' or '"')
                {
                    quote = c;
                    i++;
                    continue;
                }
                if (c == '[')
                {
                    int close = s.IndexOf(']', i + 1);
                    i = close < 0 ? s.Length : close + 1;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int q = i;
                    while (q < s.Length && (char.IsLetterOrDigit(s[q]) || s[q] == '_'))
                    {
                        q++;
                    }
                    ReadOnlySpan<char> w = s.AsSpan(i, q - i);
                    if (w.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || w.Equals("EXISTS", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                    i = q;
                    continue;
                }
                i++;
            }
            return false;
        }

        private bool IsJoinStart() => IsWord("INNER") || IsWord("LEFT") || IsWord("RIGHT") || IsWord("JOIN");

        /// <summary>
        /// Parse optional trailing <c>WITH OWNERACCESS OPTION</c> and optional <c>;</c>, enforcing ACE's 3137
        /// (trailing tokens without semicolon: <c>VALUES (…), (…)</c>, <c>DELETE … ORDER BY</c>) and 3142 (tokens after
        /// semicolon: <c>INSERT …; INSERT …</c>) (r-def-expr-sql/sql-insert-values-forms, sql-delete-forms).
        /// </summary>
        private SqlFailure? FinishAction(ActionSyntax action)
        {
            if (TakeWord("WITH"))
            {
                if (TakeWord("OWNERACCESS") && TakeWord("OPTION"))
                {
                    action.WithOwnerAccess = true;
                }
                else
                {
                    return Open("NotModelled.WithClause", "WITH clause other than WITH OWNERACCESS OPTION");
                }
            }
            if (TakeSymbol(';'))
            {
                if (!AtEnd())
                {
                    // r-def-expr-sql/sql-insert-values-forms: "INSERT ...; INSERT ..." -> 3142.
                    return SqlFailure.Ace(SqlPhase.Parse, 3142, SqlMessages.CharactersAfterEnd);
                }
                return null;
            }
            if (!AtEnd())
            {
                // r-def-expr-sql/sql-insert-values-forms: VALUES (...), (...) -> 3137;
                // r-def-expr-sql/sql-delete-forms: DELETE FROM Dm ORDER BY ID -> 3137.
                return SqlFailure.Ace(SqlPhase.Parse, 3137, SqlMessages.MissingSemicolon);
            }
            return null;
        }
    }
}
