namespace CalcDb.Sql;

/// <summary>The Access SQL DDL statement forms (W-07/native-metadata-write stretch 2; II-11 §28).</summary>
public enum DdlKind : byte
{
    CreateTable,
    CreateIndex,
    DropTable,
    DropIndex,
    AlterTable,
}

/// <summary>
/// Data types recognised by ACE's DAO ANSI-89 SQL DDL parser (evidence/r-def-expr-sql/ddl-type-names-accdb).
/// <see cref="BigInt"/> and <see cref="DateTime2"/> parse syntactically (ddl-type-names-accdb Dt69/Dt70 succeed in ACCDB; 3001 is MDB only).
/// </summary>
public enum DdlTypeKind : byte
{
    Byte,
    Short,
    Long,
    Counter,
    Single,
    Double,
    Currency,
    DateTime,
    Text,
    Memo,
    YesNo,
    Guid,
    Binary,
    VarBinary,
    LongBinary,
    Numeric,
    BigInt,
    DateTime2,
}

/// <summary>A parsed column data type with optional Text/Binary length or Counter (seed, increment).</summary>
public sealed record DdlDataType(DdlTypeKind Kind, int? Size = null, int? Seed = null, int? Increment = null);

/// <summary>Column nullability clause (<c>NULL</c>, <c>NOT NULL</c>, or omitted).</summary>
public enum DdlNullability : byte
{
    Unspecified,
    Null,
    NotNull,
}

/// <summary>Kind of inline column constraint or table constraint.</summary>
public enum DdlConstraintKind : byte
{
    PrimaryKey,
    Unique,
    ForeignKey,
    Check,
}

/// <summary>A column-level constraint (<c>[CONSTRAINT name] PRIMARY KEY | UNIQUE | REFERENCES t [(c, …)] [ON DELETE CASCADE]</c>).</summary>
public sealed record DdlColumnConstraint(
    string? Name,
    DdlConstraintKind Kind,
    string? RefTable = null,
    IReadOnlyList<string>? RefColumns = null,
    bool OnDeleteCascade = false);

/// <summary>One column definition in <c>CREATE TABLE</c> or <c>ALTER TABLE ADD</c>.</summary>
public sealed record DdlColumnDef(
    string Name,
    DdlDataType DataType,
    DdlNullability Nullability,
    IReadOnlyList<DdlColumnConstraint> Constraints);

/// <summary>A table-level constraint in <c>CREATE TABLE</c> or <c>ALTER TABLE ADD</c>.</summary>
public sealed record DdlTableConstraint(
    string? Name,
    DdlConstraintKind Kind,
    IReadOnlyList<string> Columns,
    string? RefTable = null,
    IReadOnlyList<string>? RefColumns = null,
    string? CheckExpression = null,
    bool OnDeleteCascade = false);

/// <summary>One key column in <c>CREATE [UNIQUE] INDEX … (col [ASC|DESC], …)</c>.</summary>
public sealed record DdlIndexColumn(string Name, bool Descending);

/// <summary>Options in <c>CREATE [UNIQUE] INDEX … WITH …</c> (evidence/r-def-expr-sql/ddl-index-forms).</summary>
[Flags]
public enum DdlIndexOptions : byte
{
    None = 0,
    Primary = 1,
    DisallowNull = 2,
    IgnoreNull = 4,
}

/// <summary>Optional trailing keyword on <c>DROP TABLE</c> (evidence/r-def-expr-sql/ddl-drop-and-missing-objects).</summary>
public enum DdlDropBehavior : byte
{
    Unspecified,
    Restrict,
    Cascade,
}

/// <summary>Base record for parsed Access SQL DDL statements.</summary>
public abstract record DdlStatement(string Source, DdlKind Kind);

/// <summary><c>CREATE TABLE name [(col_or_constraint, …)]</c>.</summary>
public sealed record CreateTableStatement(
    string Source,
    string TableName,
    IReadOnlyList<DdlColumnDef> Columns,
    IReadOnlyList<DdlTableConstraint> TableConstraints)
    : DdlStatement(Source, DdlKind.CreateTable);

/// <summary><c>CREATE [UNIQUE] INDEX name ON table (col [ASC|DESC], …) [WITH …]</c>.</summary>
public sealed record CreateIndexStatement(
    string Source,
    bool Unique,
    string IndexName,
    string TableName,
    IReadOnlyList<DdlIndexColumn> Columns,
    DdlIndexOptions Options)
    : DdlStatement(Source, DdlKind.CreateIndex);

/// <summary><c>DROP TABLE table [, …] [RESTRICT | CASCADE]</c>.</summary>
public sealed record DropTableStatement(
    string Source,
    IReadOnlyList<string> TableNames,
    DdlDropBehavior Behavior)
    : DdlStatement(Source, DdlKind.DropTable);

/// <summary><c>DROP INDEX name ON table</c>.</summary>
public sealed record DropIndexStatement(
    string Source,
    string IndexName,
    string TableName)
    : DdlStatement(Source, DdlKind.DropIndex);

/// <summary>Base record for <c>ALTER TABLE</c> sub-clauses.</summary>
public abstract record DdlAlterAction;

/// <summary><c>ALTER TABLE t ADD [COLUMN] col_def [, [COLUMN] col_def …]</c>.</summary>
public sealed record AddColumnsAlterAction(IReadOnlyList<DdlColumnDef> Columns) : DdlAlterAction;

/// <summary><c>ALTER TABLE t ADD [CONSTRAINT name] PRIMARY KEY | UNIQUE | FOREIGN KEY …</c>.</summary>
public sealed record AddConstraintAlterAction(DdlTableConstraint Constraint) : DdlAlterAction;

/// <summary><c>ALTER TABLE t ALTER [COLUMN] name type [NOT NULL | NULL]</c>.</summary>
public sealed record AlterColumnAlterAction(string ColumnName, DdlDataType DataType, DdlNullability Nullability) : DdlAlterAction;

/// <summary><c>ALTER TABLE t DROP [COLUMN] name [, [COLUMN] name …]</c>.</summary>
public sealed record DropColumnsAlterAction(IReadOnlyList<string> ColumnNames) : DdlAlterAction;

/// <summary><c>ALTER TABLE t DROP CONSTRAINT name</c>.</summary>
public sealed record DropConstraintAlterAction(string ConstraintName) : DdlAlterAction;

/// <summary><c>ALTER TABLE name action</c>.</summary>
public sealed record AlterTableStatement(
    string Source,
    string TableName,
    DdlAlterAction Action)
    : DdlStatement(Source, DdlKind.AlterTable);

/// <summary>
/// Access DAO ANSI-89 SQL DDL parser (W-07/native-metadata-write stretch 2; II-11 §28).
/// Covers <c>CREATE TABLE</c>, <c>CREATE [UNIQUE] INDEX</c>, <c>DROP TABLE</c>, <c>DROP INDEX</c>, and
/// <c>ALTER TABLE</c>, matching ACE's exact syntax error numbers (3129, 3289, 3290, 3291, 3292, 3293, 3295)
/// as recorded in <c>evidence/r-def-expr-sql/ddl-*</c> and <c>evidence/r-sql-2-3/act-ddl-*</c>.
/// </summary>
public static class DdlParser
{
    /// <summary>
    /// The 34 unbracketed words ACE rejects as table names in <c>CREATE TABLE</c> with 3290
    /// (evidence/r-def-expr-sql/ddl-reserved-table-names).
    /// </summary>
    private static readonly HashSet<string> ReservedTableNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATE", "TEXT", "VALUE", "TABLE", "KEY", "TIME", "MEMO", "CURRENCY", "NUMBER", "INDEX", "NOTE",
        "DESC", "ASC", "NULL", "COLUMN", "BINARY", "SHORT", "LONG", "SINGLE", "DOUBLE", "INTEGER", "BYTE",
        "TIMESTAMP", "PRIMARY", "UNIQUE", "CONSTRAINT", "CREATE", "DROP", "ALTER", "ADD", "ON", "AS", "NOT",
        "COUNTER",
    };

    /// <summary>
    /// The 31 non-constraint words ACE rejects as unbracketed column names in <c>CREATE TABLE</c> with 3292
    /// (evidence/r-def-expr-sql/ddl-reserved-column-names; PRIMARY, UNIQUE, and CONSTRAINT enter the constraint
    /// grammar and fail 3289; FOREIGN was not probed and is refused by name when used as a bare column name).
    /// </summary>
    private static readonly HashSet<string> ReservedColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DATE", "TEXT", "VALUE", "TABLE", "KEY", "TIME", "MEMO", "CURRENCY", "NUMBER", "INDEX", "NOTE",
        "DESC", "ASC", "NULL", "COLUMN", "BINARY", "SHORT", "LONG", "SINGLE", "DOUBLE", "INTEGER", "BYTE",
        "TIMESTAMP", "CREATE", "DROP", "ALTER", "ADD", "ON", "AS", "NOT", "COUNTER",
    };

    /// <summary>DDL data type keywords accepted in DAO ANSI-89 mode (evidence/r-def-expr-sql/ddl-type-names-accdb).</summary>
    private static readonly Dictionary<string, DdlTypeKind> TypeKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BYTE"] = DdlTypeKind.Byte,
        ["INTEGER1"] = DdlTypeKind.Byte,
        ["SHORT"] = DdlTypeKind.Short,
        ["SMALLINT"] = DdlTypeKind.Short,
        ["INTEGER2"] = DdlTypeKind.Short,
        ["LONG"] = DdlTypeKind.Long,
        ["INT"] = DdlTypeKind.Long,
        ["INTEGER"] = DdlTypeKind.Long,
        ["INTEGER4"] = DdlTypeKind.Long,
        ["COUNTER"] = DdlTypeKind.Counter,
        ["AUTOINCREMENT"] = DdlTypeKind.Counter,
        ["SINGLE"] = DdlTypeKind.Single,
        ["FLOAT4"] = DdlTypeKind.Single,
        ["REAL"] = DdlTypeKind.Single,
        ["IEEESINGLE"] = DdlTypeKind.Single,
        ["DOUBLE"] = DdlTypeKind.Double,
        ["FLOAT"] = DdlTypeKind.Double,
        ["FLOAT8"] = DdlTypeKind.Double,
        ["IEEEDOUBLE"] = DdlTypeKind.Double,
        ["NUMBER"] = DdlTypeKind.Double,
        ["CURRENCY"] = DdlTypeKind.Currency,
        ["MONEY"] = DdlTypeKind.Currency,
        ["DATE"] = DdlTypeKind.DateTime,
        ["DATETIME"] = DdlTypeKind.DateTime,
        ["TIME"] = DdlTypeKind.DateTime,
        ["TIMESTAMP"] = DdlTypeKind.DateTime,
        ["TEXT"] = DdlTypeKind.Text,
        ["CHAR"] = DdlTypeKind.Text,
        ["STRING"] = DdlTypeKind.Text,
        ["VARCHAR"] = DdlTypeKind.Text,
        ["ALPHANUMERIC"] = DdlTypeKind.Text,
        ["MEMO"] = DdlTypeKind.Memo,
        ["NOTE"] = DdlTypeKind.Memo,
        ["LONGTEXT"] = DdlTypeKind.Memo,
        ["LONGCHAR"] = DdlTypeKind.Memo,
        ["YESNO"] = DdlTypeKind.YesNo,
        ["BIT"] = DdlTypeKind.YesNo,
        ["LOGICAL"] = DdlTypeKind.YesNo,
        ["LOGICAL1"] = DdlTypeKind.YesNo,
        ["GUID"] = DdlTypeKind.Guid,
        ["BINARY"] = DdlTypeKind.Binary,
        ["VARBINARY"] = DdlTypeKind.VarBinary,
        ["LONGBINARY"] = DdlTypeKind.LongBinary,
        ["GENERAL"] = DdlTypeKind.LongBinary,
        ["IMAGE"] = DdlTypeKind.LongBinary,
        ["OLEOBJECT"] = DdlTypeKind.LongBinary,
        ["NUMERIC"] = DdlTypeKind.Numeric,
        ["BIGINT"] = DdlTypeKind.BigInt,
        ["DATETIME2"] = DdlTypeKind.DateTime2,
    };

    /// <summary>Whether <paramref name="source"/> starts with a DDL/DCL keyword routed to the DDL parser.</summary>
    public static bool IsDdlLeadingKeyword(string source)
    {
        int nul = source.IndexOf('\0', StringComparison.Ordinal);
        ReadOnlySpan<char> text = (nul >= 0 ? source.AsSpan(0, nul) : source.AsSpan()).TrimStart();
        int end = 0;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }
        if (end == 0)
        {
            return false;
        }
        ReadOnlySpan<char> lead = text[..end];
        return lead.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("DROP", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("REVOKE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parse a DDL statement; returns null with ACE's syntax error on failure.
    /// When <paramref name="allowAdoCheck"/> is true (ADO route), <c>ALTER TABLE ... ADD CONSTRAINT ... CHECK (...)</c>
    /// parses to <see cref="DdlConstraintKind.Check"/> and <c>CHECK</c> inside <c>CREATE TABLE</c> refuses by name;
    /// when false (DAO/SQL ANSI-89 route), <c>CHECK</c> keeps its recorded DAO 3289/3290 syntax errors.
    /// </summary>
    public static DdlStatement? Parse(string source, out SqlFailure? failure, bool allowAdoCheck = false)
    {
        if (!SqlParser.NormalizeSource(ref source, out failure))
        {
            return null;
        }
        var p = new Parser(source, allowAdoCheck);
        return p.Statement(out failure);
    }

    private sealed class Parser(string s, bool allowAdoCheck)
    {
        private enum IdentifierMode : byte
        {
            CreateTableName,
            CreateTableColumnName,
            OtherDdlName,
        }

        private int _p;

        private static SqlFailure Syntax(int number, string message) => SqlFailure.Ace(SqlPhase.Parse, number, message);

        private static SqlFailure Open(string name, string detail) => SqlFailure.Open(SqlPhase.Parse, name, detail);

        private static bool IsUnprobedTypeKeyword(string word) =>
            TypeKeywords.ContainsKey(word) && !ReservedTableNames.Contains(word);

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

        private bool IsBracketed()
        {
            Skip();
            return _p < s.Length && s[_p] is '[' or '`';
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

        /// <summary>
        /// Read a DDL identifier (bracketed <c>[…]</c>, backticked <c>`…`</c>, or bare word).
        /// Matches <c>r-def-expr-sql/ddl-name-and-size-limits</c>: empty <c>[]</c>, unterminated <c>[</c>, and
        /// backtick inside <c>[…]</c> fail at parse time; valid bracket contents containing <c>. ! \t \r\n</c> or
        /// leading space pass parse and fail 3125 at bind.
        /// Bare reserved words fail with recorded ACE syntax errors only at CREATE TABLE table/column name sites;
        /// unprobed type/DDL keywords and all other DDL identifier sites refuse by name (<c>NotModelled.DdlReservedWord</c>).
        /// </summary>
        private string? ReadIdentifier(IdentifierMode mode, out SqlFailure? notModelled)
        {
            notModelled = null;
            Skip();
            if (_p >= s.Length)
            {
                return null;
            }
            if (s[_p] is '[' or '`')
            {
                char open = s[_p];
                char close = open == '[' ? ']' : '`';
                int end = s.IndexOf(close, _p + 1);
                if (end < 0)
                {
                    return null;
                }
                string inside = s[(_p + 1)..end];
                // ddl-name-and-size-limits: [] and [T`T] fail 3290 at parse; [a]]b] closes at first ] and leaves b].
                if (inside.Length == 0)
                {
                    return null;
                }
                if (open == '[' && inside.Contains('`'))
                {
                    return null;
                }
                if (open == '`' && (inside.Contains('[') || inside.Contains(']')))
                {
                    return null;
                }
                _p = end + 1;
                return inside;
            }
            string? word = PeekWord();
            if (word is null)
            {
                return null;
            }
            if (mode == IdentifierMode.CreateTableName)
            {
                if (ReservedTableNames.Contains(word))
                {
                    return null;
                }
                if (IsUnprobedTypeKeyword(word) || word.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase))
                {
                    notModelled = Open("NotModelled.DdlReservedWord", $"unprobed keyword '{word}' used as an unbracketed CREATE TABLE name");
                    return null;
                }
            }
            else if (mode == IdentifierMode.CreateTableColumnName)
            {
                if (ReservedColumnNames.Contains(word))
                {
                    return null;
                }
                if (IsUnprobedTypeKeyword(word) || word.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase))
                {
                    notModelled = Open("NotModelled.DdlReservedWord", $"unprobed keyword '{word}' used as an unbracketed CREATE TABLE column name");
                    return null;
                }
            }
            else
            {
                if (ReservedTableNames.Contains(word) || IsUnprobedTypeKeyword(word) || word.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase))
                {
                    notModelled = Open("NotModelled.DdlReservedWord", $"reserved or unprobed keyword '{word}' used as an unbracketed DDL identifier outside CREATE TABLE");
                    return null;
                }
            }
            _p += word.Length;
            return word;
        }

        private bool TryReadSignedInt(out int value)
        {
            Skip();
            int start = _p;
            if (_p < s.Length && s[_p] is '+' or '-')
            {
                _p++;
            }
            int digits = _p;
            while (_p < s.Length && char.IsAsciiDigit(s[_p]))
            {
                _p++;
            }
            if (_p == digits)
            {
                _p = start;
                value = 0;
                return false;
            }
            // Reject decimal fractions like 1.5 (r-def-expr-sql/ddl-counter-seed-increment: Cs8 -> 3292).
            if (_p < s.Length && s[_p] == '.')
            {
                _p = start;
                value = 0;
                return false;
            }
            if (!int.TryParse(s[start.._p], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                _p = start;
                return false;
            }
            return true;
        }

        private bool TryReadNonNegativeInt(out int value)
        {
            Skip();
            int start = _p;
            while (_p < s.Length && char.IsAsciiDigit(s[_p]))
            {
                _p++;
            }
            if (_p == start || (_p < s.Length && s[_p] == '.'))
            {
                _p = start;
                value = 0;
                return false;
            }
            if (!int.TryParse(s[start.._p], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value))
            {
                _p = start;
                return false;
            }
            return true;
        }

        private SqlFailure? ExpectEnd(int trailingNumber, string trailingMessage)
        {
            if (AtEnd())
            {
                return null;
            }
            if (TakeSymbol(';'))
            {
                return AtEnd()
                    ? null
                    : Open("NotModelled.DdlTrailingAfterSemicolon", "tokens after ';' in DDL statement (only terminating ';' is recorded)");
            }
            return Syntax(trailingNumber, trailingMessage);
        }

        public DdlStatement? Statement(out SqlFailure? failure)
        {
            if (TakeWord("CREATE"))
            {
                return ParseCreate(out failure);
            }
            if (TakeWord("DROP"))
            {
                return ParseDrop(out failure);
            }
            if (TakeWord("ALTER"))
            {
                return ParseAlter(out failure);
            }
            // r-sql-2-3/act-ddl-objects: GRANT and REVOKE fail 3129 in DAO ANSI-89 mode.
            failure = Syntax(3129, SqlMessages.InvalidSqlStatement);
            return null;
        }

        // ---- CREATE TABLE / CREATE [UNIQUE] INDEX ------------------------------------------------------------------

        private DdlStatement? ParseCreate(out SqlFailure? failure)
        {
            if (TakeWord("TABLE"))
            {
                return ParseCreateTable(out failure);
            }
            if (TakeWord("UNIQUE"))
            {
                if (TakeWord("INDEX"))
                {
                    return ParseCreateIndex(unique: true, out failure);
                }
                failure = Syntax(3290, SqlMessages.CreateTableSyntaxError);
                return null;
            }
            if (TakeWord("INDEX"))
            {
                return ParseCreateIndex(unique: false, out failure);
            }
            // r-sql-2-3/act-ddl-objects, ddl-index-forms, sql-unsupported-statements:
            // CREATE VIEW / PROCEDURE / USER / GROUP / TEMPORARY TABLE / CLUSTERED|HASH|FULLTEXT INDEX / DATABASE -> 3290.
            failure = Syntax(3290, SqlMessages.CreateTableSyntaxError);
            return null;
        }

        private CreateTableStatement? ParseCreateTable(out SqlFailure? failure)
        {
            string? tableName = ReadIdentifier(IdentifierMode.CreateTableName, out SqlFailure? reservedFail);
            if (tableName is null)
            {
                failure = reservedFail ?? Syntax(3290, SqlMessages.CreateTableSyntaxError);
                return null;
            }

            var columns = new List<DdlColumnDef>();
            var constraints = new List<DdlTableConstraint>();

            if (TakeSymbol('('))
            {
                if (IsSymbol(')'))
                {
                    // r-def-expr-sql/ddl-column-clauses: CREATE TABLE Cc18 () -> 3292.
                    failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                    return null;
                }

                while (true)
                {
                    if (IsSymbol(')') || AtEnd())
                    {
                        // r-def-expr-sql/ddl-column-clauses: CREATE TABLE Cc17 (A LONG,) -> 3292.
                        failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                        return null;
                    }

                    if (!IsBracketed() && IsTableConstraintLead())
                    {
                        DdlTableConstraint? tc = ParseTableConstraint(inAlter: false, out failure);
                        if (tc is null)
                        {
                            return null;
                        }
                        constraints.Add(tc);
                    }
                    else
                    {
                        DdlColumnDef? col = ParseColumnDef(isAlter: false, out failure);
                        if (col is null)
                        {
                            return null;
                        }
                        columns.Add(col);
                    }

                    if (TakeSymbol(','))
                    {
                        continue;
                    }
                    if (TakeSymbol(')'))
                    {
                        break;
                    }
                    // r-def-expr-sql/ddl-column-clauses: missing comma (Cc23, Cc35) or trailing unsupported clause -> 3290.
                    failure = Syntax(3290, SqlMessages.CreateTableSyntaxError);
                    return null;
                }
            }

            failure = ExpectEnd(3290, SqlMessages.CreateTableSyntaxError);
            if (failure is not null)
            {
                return null;
            }
            return new CreateTableStatement(s, tableName, columns, constraints);
        }

        private bool IsTableConstraintLead()
        {
            string? w = PeekWord();
            if (w is null)
            {
                return false;
            }
            if (w.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase)
                || w.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase)
                || w.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (w.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase))
            {
                // FOREIGN KEY (...) starts a table constraint; bare FOREIGN as a column name was not probed and
                // routes to ReadIdentifier -> NotModelled.DdlReservedWord (B7).
                int saved = _p;
                Skip();
                _p += w.Length;
                bool followedByKey = IsWord("KEY");
                _p = saved;
                return followedByKey;
            }
            if (allowAdoCheck && w.Equals("CHECK", StringComparison.OrdinalIgnoreCase))
            {
                int saved = _p;
                Skip();
                _p += w.Length;
                bool followedByParen = IsSymbol('(');
                _p = saved;
                return followedByParen;
            }
            return false;
        }

        private DdlColumnDef? ParseColumnDef(bool isAlter, out SqlFailure? failure)
        {
            string? colName = ReadIdentifier(isAlter ? IdentifierMode.OtherDdlName : IdentifierMode.CreateTableColumnName, out SqlFailure? reservedFail);
            if (colName is null)
            {
                failure = reservedFail ?? Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                return null;
            }

            DdlDataType? dataType = ParseDataType(isAlter, out failure);
            if (dataType is null)
            {
                return null;
            }

            DdlNullability nullability = DdlNullability.Unspecified;
            var constraints = new List<DdlColumnConstraint>();

            while (!AtEnd() && !IsSymbol(',') && !IsSymbol(')') && !IsSymbol(';'))
            {
                if (TakeWord("NOT"))
                {
                    if (!TakeWord("NULL"))
                    {
                        failure = Open("NotModelled.DdlNotWithoutNull", "NOT without NULL in column definition");
                        return null;
                    }
                    if (nullability != DdlNullability.Unspecified)
                    {
                        // r-def-expr-sql/ddl-column-clauses: Cc34 (A LONG NOT NULL NULL) -> 3292.
                        failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                        return null;
                    }
                    nullability = DdlNullability.NotNull;
                    continue;
                }
                if (TakeWord("NULL"))
                {
                    if (nullability != DdlNullability.Unspecified)
                    {
                        failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                        return null;
                    }
                    nullability = DdlNullability.Null;
                    continue;
                }
                if (TakeWord("CONSTRAINT"))
                {
                    string? constraintName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? conNameFail);
                    if (constraintName is null)
                    {
                        failure = conNameFail ?? Open("NotModelled.DdlConstraintSyntax", "missing or malformed constraint name after CONSTRAINT");
                        return null;
                    }
                    DdlColumnConstraint? cc = ParseColumnConstraintBody(constraintName, isAlter, out failure);
                    if (cc is null)
                    {
                        return null;
                    }
                    constraints.Add(cc);
                    continue;
                }
                if (IsWord("PRIMARY") || IsWord("UNIQUE") || IsWord("REFERENCES"))
                {
                    DdlColumnConstraint? cc = ParseColumnConstraintBody(null, isAlter, out failure);
                    if (cc is null)
                    {
                        return null;
                    }
                    constraints.Add(cc);
                    continue;
                }
                if (allowAdoCheck && IsWord("CHECK"))
                {
                    if (!isAlter)
                    {
                        // evidence/r-q-cat-txn-lock/ii11-ado-check-constraint seq 23:
                        // CREATE TABLE TC (ID INTEGER, W INTEGER CHECK (W > 0)) -> scode 0x80040E14, helpContext 5003292
                        // (ACE 3292 "Syntax error in field definition."). Named inline and table-level CHECK in CREATE TABLE
                        // remain refused by name (NotModelled.DdlCreateTableCheck).
                        failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                        return null;
                    }
                    failure = Open(
                        "NotModelled.DdlAlterColumnCheck",
                        "column-level CHECK constraint in ALTER TABLE ADD COLUMN via ADO is not modelled");
                    return null;
                }

                // Unsupported column modifiers (DEFAULT, WITH COMPRESSION, CHECK, COLLATE, COMMENT, AUTO_INCREMENT,
                // IDENTITY, UNSIGNED, GENERATED, UNICODE, BINARY VARYING) or missing comma between columns:
                // 3290 in CREATE TABLE (ddl-column-clauses, ddl-type-names-accdb), 3293 in ALTER TABLE (ddl-alter-column-forms).
                failure = Syntax(isAlter ? 3293 : 3290, isAlter ? SqlMessages.AlterTableSyntaxError : SqlMessages.CreateTableSyntaxError);
                return null;
            }

            failure = null;
            return new DdlColumnDef(colName, dataType, nullability, constraints);
        }

        private DdlDataType? ParseDataType(bool isAlter, out SqlFailure? failure)
        {
            SqlFailure InvalidType() => isAlter
                ? Open("NotModelled.DdlAlterTypeSyntax", "invalid type or type parameter in ALTER TABLE")
                : Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);

            SqlFailure IllegalParam() => isAlter
                ? Open("NotModelled.DdlAlterTypeSyntax", "illegal type parameter in ALTER TABLE")
                : Syntax(3290, SqlMessages.CreateTableSyntaxError);

            string? word = PeekWord();
            if (word is null || !TypeKeywords.TryGetValue(word, out DdlTypeKind kind))
            {
                failure = InvalidType();
                return null;
            }
            _p += word.Length;

            if (kind is DdlTypeKind.Text or DdlTypeKind.Binary or DdlTypeKind.VarBinary)
            {
                if (TakeSymbol('('))
                {
                    if (!TryReadNonNegativeInt(out int size) || !TakeSymbol(')'))
                    {
                        failure = InvalidType();
                        return null;
                    }
                    failure = null;
                    return new DdlDataType(kind, Size: size);
                }
                failure = null;
                return new DdlDataType(kind);
            }

            if (kind == DdlTypeKind.Counter)
            {
                if (TakeSymbol('('))
                {
                    if (!TryReadSignedInt(out int seed))
                    {
                        failure = InvalidType();
                        return null;
                    }
                    int? increment = null;
                    if (TakeSymbol(','))
                    {
                        if (!TryReadSignedInt(out int inc))
                        {
                            failure = InvalidType();
                            return null;
                        }
                        increment = inc;
                    }
                    if (!TakeSymbol(')'))
                    {
                        failure = InvalidType();
                        return null;
                    }
                    failure = null;
                    return new DdlDataType(kind, Seed: seed, Increment: increment);
                }
                failure = null;
                return new DdlDataType(kind);
            }

            // Other types (LONG, NUMERIC, DATETIME2, etc.) reject parenthesised parameters in DAO ANSI-89 mode
            // (r-def-expr-sql/ddl-type-names-accdb: LONG(10), NUMERIC(10, 2), DATETIME2(3) -> 3290 in CREATE TABLE).
            if (IsSymbol('('))
            {
                failure = IllegalParam();
                return null;
            }

            failure = null;
            return new DdlDataType(kind);
        }

        private DdlColumnConstraint? ParseColumnConstraintBody(string? name, bool isAlter, out SqlFailure? failure)
        {
            if (TakeWord("PRIMARY"))
            {
                if (!TakeWord("KEY"))
                {
                    failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return null;
                }
                failure = null;
                return new DdlColumnConstraint(name, DdlConstraintKind.PrimaryKey);
            }
            if (TakeWord("UNIQUE"))
            {
                failure = null;
                return new DdlColumnConstraint(name, DdlConstraintKind.Unique);
            }
            if (TakeWord("REFERENCES"))
            {
                if (!ParseReferencesTarget(out string? refTable, out List<string>? refCols, out bool onDeleteCascade, out failure))
                {
                    return null;
                }
                return new DdlColumnConstraint(name, DdlConstraintKind.ForeignKey, refTable, refCols, onDeleteCascade);
            }
            if (allowAdoCheck && IsWord("CHECK"))
            {
                // Named inline CHECK in CREATE TABLE (or ALTER TABLE ADD COLUMN) via ADO is not recorded; DAO keeps 3289.
                failure = Open(
                    isAlter ? "NotModelled.DdlAlterColumnCheck" : "NotModelled.DdlCreateTableCheck",
                    isAlter
                        ? "column-level CHECK constraint in ALTER TABLE ADD COLUMN via ADO is not modelled"
                        : "CHECK constraint inside CREATE TABLE via ADO is not modelled");
                return null;
            }
            failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
            return null;
        }

        private DdlTableConstraint? ParseTableConstraint(bool inAlter, out SqlFailure? failure)
        {
            failure = null;
            string? name = null;
            if (TakeWord("CONSTRAINT"))
            {
                name = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? conNameFail);
                if (name is null)
                {
                    failure = conNameFail ?? Open("NotModelled.DdlConstraintSyntax", "missing or malformed constraint name after CONSTRAINT");
                    return null;
                }
            }

            if (TakeWord("PRIMARY"))
            {
                if (!TakeWord("KEY") || !ParseConstraintColumnList(out List<string>? cols, out failure))
                {
                    failure ??= Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return null;
                }
                return new DdlTableConstraint(name, DdlConstraintKind.PrimaryKey, cols!);
            }
            if (TakeWord("UNIQUE"))
            {
                if (!ParseConstraintColumnList(out List<string>? cols, out failure))
                {
                    return null;
                }
                return new DdlTableConstraint(name, DdlConstraintKind.Unique, cols!);
            }
            if (TakeWord("FOREIGN"))
            {
                if (!TakeWord("KEY") || !ParseConstraintColumnList(out List<string>? cols, out failure))
                {
                    failure ??= Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return null;
                }
                if (!TakeWord("REFERENCES") || !ParseReferencesTarget(out string? refTable, out List<string>? refCols, out bool onDeleteCascade, out failure))
                {
                    failure ??= Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return null;
                }
                return new DdlTableConstraint(name, DdlConstraintKind.ForeignKey, cols!, refTable, refCols, OnDeleteCascade: onDeleteCascade);
            }
            if (TakeWord("CHECK"))
            {
                if (!allowAdoCheck)
                {
                    // DAO ANSI-89 mode: CONSTRAINT ... CHECK (...) -> 3289 (r-def-expr-sql/ddl-column-clauses: Cc15;
                    // evidence/r-q34-ddl-sweep/alt-check-empty-accdb-dao).
                    failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return null;
                }
                if (!inAlter)
                {
                    failure = Open("NotModelled.DdlCreateTableCheck", "CHECK constraint inside CREATE TABLE via ADO is not modelled");
                    return null;
                }
                if (!ParseCheckExpression(out string? expr, out failure))
                {
                    return null;
                }
                return new DdlTableConstraint(name, DdlConstraintKind.Check, [], CheckExpression: expr);
            }

            // Includes unrecognized constraint forms -> 3289.
            failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
            return null;
        }

        private bool ParseCheckExpression(out string? expression, out SqlFailure? failure)
        {
            expression = null;
            if (!TakeSymbol('('))
            {
                failure = Open("NotModelled.DdlCheckSyntax", "expected '(' after CHECK");
                return false;
            }
            int start = _p;
            int depth = 1;
            while (_p < s.Length && depth > 0)
            {
                char ch = s[_p];
                if (ch == '\'')
                {
                    _p++;
                    bool closed = false;
                    while (_p < s.Length)
                    {
                        if (s[_p] == '\'')
                        {
                            _p++;
                            if (_p < s.Length && s[_p] == '\'')
                            {
                                _p++;
                                continue;
                            }
                            closed = true;
                            break;
                        }
                        _p++;
                    }
                    if (!closed)
                    {
                        failure = Open("NotModelled.DdlCheckSyntax", "unterminated string literal in CHECK expression");
                        return false;
                    }
                    continue;
                }
                if (ch == '[')
                {
                    int close = s.IndexOf(']', _p + 1);
                    if (close < 0)
                    {
                        failure = Open("NotModelled.DdlCheckSyntax", "unterminated bracketed identifier in CHECK expression");
                        return false;
                    }
                    _p = close + 1;
                    continue;
                }
                if (ch == '`')
                {
                    int close = s.IndexOf('`', _p + 1);
                    if (close < 0)
                    {
                        failure = Open("NotModelled.DdlCheckSyntax", "unterminated backtick identifier in CHECK expression");
                        return false;
                    }
                    _p = close + 1;
                    continue;
                }
                if (ch == '(')
                {
                    depth++;
                    _p++;
                    continue;
                }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                    _p++;
                    continue;
                }
                _p++;
            }
            if (depth != 0)
            {
                failure = Open("NotModelled.DdlCheckSyntax", "unterminated '(' in CHECK expression");
                return false;
            }
            string body = s[start.._p].Trim();
            _p++; // consume matching ')'
            if (body.Length == 0)
            {
                failure = Open("NotModelled.DdlCheckSyntax", "empty CHECK expression");
                return false;
            }
            expression = body;
            failure = null;
            return true;
        }

        private bool ParseConstraintColumnList(out List<string>? cols, out SqlFailure? failure)
        {
            cols = null;
            if (!TakeSymbol('('))
            {
                failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
                return false;
            }
            var list = new List<string>();
            while (true)
            {
                string? col = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? reservedFail);
                if (col is null)
                {
                    failure = reservedFail ?? Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return false;
                }
                list.Add(col);
                if (TakeSymbol(','))
                {
                    continue;
                }
                if (TakeSymbol(')'))
                {
                    break;
                }
                // Includes ASC/DESC inside PRIMARY KEY (...) -> 3289 (ddl-column-clauses: Cc8).
                failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
                return false;
            }
            cols = list;
            failure = null;
            return true;
        }

        private bool ParseReferencesTarget(out string? refTable, out List<string>? refCols, out bool onDeleteCascade, out SqlFailure? failure)
        {
            refTable = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? reservedFail);
            refCols = null;
            onDeleteCascade = false;
            if (refTable is null)
            {
                failure = reservedFail ?? Syntax(3289, SqlMessages.ConstraintSyntaxError);
                return false;
            }
            if (IsSymbol('('))
            {
                if (!ParseConstraintColumnList(out refCols, out failure))
                {
                    return false;
                }
            }
            if (IsWord("ON"))
            {
                if (!allowAdoCheck)
                {
                    // ON DELETE / ON UPDATE actions fail 3289 in DAO ANSI-89 mode
                    // (r-def-expr-sql/ddl-fk-actions; evidence/r-q34-ddl-sweep/fk-cascade-*-dao).
                    failure = Syntax(3289, SqlMessages.ConstraintSyntaxError);
                    return false;
                }
                TakeWord("ON");
                if (TakeWord("DELETE") && TakeWord("CASCADE"))
                {
                    if (IsWord("ON"))
                    {
                        failure = Open("NotModelled.DdlForeignKeyAction", "combining ON DELETE CASCADE with another ON referential action is not recorded");
                        return false;
                    }
                    onDeleteCascade = true;
                }
                else
                {
                    failure = Open("NotModelled.DdlForeignKeyAction", "FOREIGN KEY referential action other than ADO ON DELETE CASCADE is not recorded (ON UPDATE CASCADE, SET NULL, NO ACTION, SET DEFAULT, RESTRICT)");
                    return false;
                }
            }
            failure = null;
            return true;
        }

        private CreateIndexStatement? ParseCreateIndex(bool unique, out SqlFailure? failure)
        {
            if (IsWord("ON"))
            {
                // r-def-expr-sql/ddl-index-forms: CREATE INDEX ON Dm (DT) -> 3291.
                failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                return null;
            }
            string? indexName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? idxNameFail);
            if (indexName is null)
            {
                failure = idxNameFail ?? Open("NotModelled.DdlCreateIndexSyntax", "missing or malformed index name in CREATE INDEX");
                return null;
            }
            if (!TakeWord("ON"))
            {
                failure = Open("NotModelled.DdlCreateIndexSyntax", "missing ON keyword in CREATE INDEX");
                return null;
            }
            string? tableName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? tblNameFail);
            if (tableName is null)
            {
                failure = tblNameFail ?? Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                return null;
            }
            if (!TakeSymbol('('))
            {
                failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                return null;
            }

            var cols = new List<DdlIndexColumn>();
            while (true)
            {
                string? col = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? colNameFail);
                if (col is null)
                {
                    failure = colNameFail ?? Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                    return null;
                }
                bool desc = false;
                if (TakeWord("DESC"))
                {
                    desc = true;
                }
                else
                {
                    TakeWord("ASC");
                }
                cols.Add(new DdlIndexColumn(col, desc));
                if (TakeSymbol(','))
                {
                    continue;
                }
                if (TakeSymbol(')'))
                {
                    break;
                }
                // Includes N DESC DESC (ddl-index-forms: ixh -> 3291).
                failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                return null;
            }

            DdlIndexOptions options = DdlIndexOptions.None;
            if (TakeWord("WITH"))
            {
                bool any = false;
                while (!AtEnd() && !IsSymbol(';'))
                {
                    if (TakeWord("PRIMARY"))
                    {
                        options |= DdlIndexOptions.Primary;
                        any = true;
                        continue;
                    }
                    if (TakeWord("DISALLOW"))
                    {
                        if (!TakeWord("NULL"))
                        {
                            failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                            return null;
                        }
                        options |= DdlIndexOptions.DisallowNull;
                        any = true;
                        continue;
                    }
                    if (TakeWord("IGNORE"))
                    {
                        if (!TakeWord("NULL"))
                        {
                            failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                            return null;
                        }
                        options |= DdlIndexOptions.IgnoreNull;
                        any = true;
                        continue;
                    }
                    failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                    return null;
                }
                if (!any)
                {
                    failure = Syntax(3291, SqlMessages.CreateIndexSyntaxError);
                    return null;
                }
            }

            failure = ExpectEnd(3291, SqlMessages.CreateIndexSyntaxError);
            if (failure is not null)
            {
                return null;
            }
            return new CreateIndexStatement(s, unique, indexName, tableName, cols, options);
        }

        // ---- DROP TABLE / DROP INDEX -------------------------------------------------------------------------------

        private DdlStatement? ParseDrop(out SqlFailure? failure)
        {
            if (TakeWord("TABLE"))
            {
                var names = new List<string>();
                while (true)
                {
                    string? name = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? nameFail);
                    if (name is null)
                    {
                        failure = nameFail ?? Open("NotModelled.DdlDropSyntax", "missing or malformed table name in DROP TABLE");
                        return null;
                    }
                    names.Add(name);
                    if (!TakeSymbol(','))
                    {
                        break;
                    }
                }
                DdlDropBehavior behavior = DdlDropBehavior.Unspecified;
                if (TakeWord("RESTRICT"))
                {
                    behavior = DdlDropBehavior.Restrict;
                }
                else if (TakeWord("CASCADE"))
                {
                    behavior = DdlDropBehavior.Cascade;
                }
                failure = ExpectEnd(3295, SqlMessages.DropSyntaxError);
                if (failure is not null)
                {
                    return null;
                }
                return new DropTableStatement(s, names, behavior);
            }

            if (TakeWord("INDEX"))
            {
                string? indexName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? idxNameFail);
                if (indexName is null)
                {
                    failure = idxNameFail ?? Syntax(3295, SqlMessages.DropSyntaxError);
                    return null;
                }
                if (!TakeWord("ON"))
                {
                    failure = Syntax(3295, SqlMessages.DropSyntaxError);
                    return null;
                }
                string? tableName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? tblNameFail);
                if (tableName is null)
                {
                    failure = tblNameFail ?? Syntax(3295, SqlMessages.DropSyntaxError);
                    return null;
                }
                failure = ExpectEnd(3295, SqlMessages.DropSyntaxError);
                if (failure is not null)
                {
                    return null;
                }
                return new DropIndexStatement(s, indexName, tableName);
            }

            // r-def-expr-sql/ddl-drop-and-missing-objects, r-sql-2-3/act-ddl-objects: DROP VIEW / PROCEDURE -> 3295.
            failure = Syntax(3295, SqlMessages.DropSyntaxError);
            return null;
        }

        // ---- ALTER TABLE -------------------------------------------------------------------------------------------

        private AlterTableStatement? ParseAlter(out SqlFailure? failure)
        {
            if (!TakeWord("TABLE"))
            {
                // r-sql-2-3/act-ddl-objects: ALTER DATABASE -> 3129.
                failure = Syntax(3129, SqlMessages.InvalidSqlStatement);
                return null;
            }
            string? tableName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? tblNameFail);
            if (tableName is null)
            {
                failure = tblNameFail ?? Syntax(3293, SqlMessages.AlterTableSyntaxError);
                return null;
            }
            if (AtEnd() || IsSymbol(';'))
            {
                // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE At -> 3293.
                failure = Syntax(3293, SqlMessages.AlterTableSyntaxError);
                return null;
            }

            DdlAlterAction? action;
            if (TakeWord("ADD"))
            {
                if (AtEnd() || IsSymbol(';'))
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE At ADD -> 3292.
                    failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                    return null;
                }
                if (!IsBracketed() && IsWord("INDEX"))
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE At ADD INDEX IXB (B) -> 3292.
                    failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                    return null;
                }
                if (!IsBracketed() && IsTableConstraintLead())
                {
                    DdlTableConstraint? tc = ParseTableConstraint(inAlter: true, out failure);
                    if (tc is null)
                    {
                        return null;
                    }
                    action = new AddConstraintAlterAction(tc);
                }
                else
                {
                    var cols = new List<DdlColumnDef>();
                    while (true)
                    {
                        TakeWord("COLUMN");
                        if (AtEnd() || IsSymbol(';'))
                        {
                            // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE At ADD COLUMN Q LONG, -> 3292.
                            failure = Syntax(3292, SqlMessages.FieldDefinitionSyntaxError);
                            return null;
                        }
                        DdlColumnDef? col = ParseColumnDef(isAlter: true, out failure);
                        if (col is null)
                        {
                            return null;
                        }
                        cols.Add(col);
                        if (!TakeSymbol(','))
                        {
                            break;
                        }
                    }
                    action = new AddColumnsAlterAction(cols);
                }
            }
            else if (TakeWord("ALTER"))
            {
                TakeWord("COLUMN");
                string? colName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? colNameFail);
                if (colName is null)
                {
                    failure = colNameFail ?? Syntax(3293, SqlMessages.AlterTableSyntaxError);
                    return null;
                }
                if (IsWord("DEFAULT") || IsWord("SET") || IsWord("DROP"))
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ALTER COLUMN B DEFAULT 5 / SET DEFAULT 5 / DROP DEFAULT -> 3293.
                    failure = Syntax(3293, SqlMessages.AlterTableSyntaxError);
                    return null;
                }
                DdlDataType? dataType = ParseDataType(isAlter: true, out failure);
                if (dataType is null)
                {
                    return null;
                }
                DdlNullability nullability = DdlNullability.Unspecified;
                if (TakeWord("NOT"))
                {
                    if (!TakeWord("NULL"))
                    {
                        failure = Open("NotModelled.DdlNotWithoutNull", "NOT without NULL in ALTER COLUMN");
                        return null;
                    }
                    nullability = DdlNullability.NotNull;
                }
                else if (TakeWord("NULL"))
                {
                    nullability = DdlNullability.Null;
                }
                action = new AlterColumnAlterAction(colName, dataType, nullability);
            }
            else if (TakeWord("DROP"))
            {
                if (TakeWord("CONSTRAINT"))
                {
                    string? constraintName = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? conNameFail);
                    if (constraintName is null)
                    {
                        failure = conNameFail ?? Syntax(3293, SqlMessages.AlterTableSyntaxError);
                        return null;
                    }
                    action = new DropConstraintAlterAction(constraintName);
                }
                else if (IsWord("PRIMARY"))
                {
                    // r-def-expr-sql/ddl-alter-column-forms: ALTER TABLE At DROP PRIMARY KEY -> 3293.
                    failure = Syntax(3293, SqlMessages.AlterTableSyntaxError);
                    return null;
                }
                else
                {
                    var cols = new List<string>();
                    while (true)
                    {
                        TakeWord("COLUMN");
                        string? col = ReadIdentifier(IdentifierMode.OtherDdlName, out SqlFailure? colNameFail);
                        if (col is null)
                        {
                            failure = colNameFail ?? Syntax(3293, SqlMessages.AlterTableSyntaxError);
                            return null;
                        }
                        cols.Add(col);
                        if (!TakeSymbol(','))
                        {
                            break;
                        }
                    }
                    action = new DropColumnsAlterAction(cols);
                }
            }
            else
            {
                // Includes ALTER TABLE At RENAME ... -> 3293.
                failure = Syntax(3293, SqlMessages.AlterTableSyntaxError);
                return null;
            }

            failure = ExpectEnd(3293, SqlMessages.AlterTableSyntaxError);
            if (failure is not null)
            {
                return null;
            }
            return new AlterTableStatement(s, tableName, action);
        }
    }
}
