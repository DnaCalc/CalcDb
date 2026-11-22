using CalcDb.Format;
using CalcDb.Values;

namespace CalcDb.Isam.Edit;

/// <summary>A validation rule's verdict on a candidate (II-10 §21 "validation adapter").</summary>
public enum RuleVerdict : byte
{
    /// <summary>True, or Null: a rule that evaluates Null admits the row (qualified.md R-EXPR-1 EXPR-MUTATION, Branch D).</summary>
    Admit,

    /// <summary>False: 3316 with the ValidationText, 3317 without it.</summary>
    Reject,

    /// <summary>The evaluation failed: 3802 (r-expr-1/mut-validation-rules).</summary>
    Failed,

    /// <summary>A result or a case no record decides (a numeric non-truth result, a not-modelled scalar).</summary>
    NotModelled,
}

/// <summary>A verdict, with the detail of a not-modelled case.</summary>
public readonly record struct RuleResult(RuleVerdict Verdict, string? Detail = null);

/// <summary>
/// A bound validation rule (II-10 §21): a field rule in its field-local context or a table rule over the candidate row. It
/// reads the candidate through the cursor interface the expression service already reads rows by, never moving it.
/// </summary>
public interface IMutationRule : IDisposable
{
    RuleResult Evaluate(IRecordCursor candidate);
}

/// <summary>
/// A bound default (II-10 §21 "Field default": a restricted builtin environment, no row or host lookup). Sampled once
/// per AddNew (qualified.md EXPR-STAGE: Now() and Rnd() are sampled at AddNew and held until Update).
/// </summary>
public interface IFieldDefault : IDisposable
{
    /// <summary>Evaluate; the value is borrowed from the default until its next evaluation. False with the failure's detail.</summary>
    bool Sample(out NativeVariant value, out string? failure);
}

/// <summary>
/// Binds the stored property texts of a table into its rules and defaults (II-10 §21: the expression service's
/// adapters). Implemented above the ISAM by the expression service; null with the reason when it does not admit a text.
/// </summary>
public interface IMutationRuleBinder
{
    IFieldDefault? BindDefault(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted);

    IMutationRule? BindFieldRule(TableDefinition table, ColumnDescriptor column, string text, out string? notAdmitted);

    IMutationRule? BindTableRule(TableDefinition table, string text, out string? notAdmitted);
}

/// <summary>A rule with its stored text (kept unchanged, trailing NUL included, II-10 §21) and its ValidationText.</summary>
public sealed record BoundRule(string StoredText, string? ValidationText, IMutationRule? Rule, string? NotAdmitted)
{
    /// <summary>The rule text as a 3317 message quotes it: the stored text without its trailing NUL.</summary>
    public string Quoted => StoredText.TrimEnd('\0');
}

/// <summary>
/// A table-level SQL <c>CHECK</c> constraint loaded from the table's MR2 <c>ObjectMap["CheckConstraints"]</c>
/// memo property (<c>name\0expression\0\0</c>; verified on <c>r-oledb-odbc-v2-accdb.accdb</c>,
/// <c>r-oledb-odbc-v2-mdb.mdb</c>, <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>).
/// Only a single-column scalar comparison against a literal on one column of the table is bound through
/// <see cref="IMutationRuleBinder"/>; any other expression stays unbound with <see cref="NotAdmitted"/> set so
/// writes refuse by name rather than silently bypassing the constraint.
/// </summary>
public sealed record BoundCheckConstraint(
    string Name,
    string Expression,
    ColumnDescriptor? Column,
    IMutationRule? Rule,
    string? NotAdmitted);

/// <summary>The checks and defaults of one column (II-07 §14 MutationSemantics, per base column).</summary>
public sealed class ColumnSemantics
{
    public required ColumnDescriptor Column { get; init; }

    /// <summary>The stored Required property (a DDL NOT NULL is stored as Required True: r-cursor-1-edit G.T).</summary>
    public bool Required { get; init; }

    /// <summary>The stored AllowZeroLength property, or null when the column has none stored.</summary>
    public bool? AllowZeroLength { get; init; }

    /// <summary>The stored DefaultValue text, or null.</summary>
    public string? DefaultText { get; init; }

    public IFieldDefault? Default { get; init; }

    /// <summary>Why the binder did not admit the default text.</summary>
    public string? DefaultNotAdmitted { get; init; }

    public BoundRule? Rule { get; init; }

    /// <summary>"Table.Field", as 3314 and 3315 name it.</summary>
    public required string QualifiedName { get; init; }
}

/// <summary>
/// A physical index of the table with its key columns in index order: the Update checks a unique or Null-refusing one
/// (3022, 3058), and a change of any one's key locks the leaf holding the row's entry (TXN-LOCAL; <c>TableFootprints</c>).
/// </summary>
public sealed record IndexCheck(LogicalIndex Logical, PhysicalIndex Physical, IReadOnlyList<ColumnDescriptor> Columns, bool Unique, bool RefusesNull);

/// <summary>
/// The compiled mutation descriptor of one table (II-07 §14 <c>MutationSemantics</c>; II-10 §21): per column its stored
/// Required and AllowZeroLength, default and field rule; the table rule; SQL <c>CHECK</c> constraints; the indexes
/// whose uniqueness or Null refusal an Update checks. Built once per table definition and property blob, never per row.
/// Its stage order is the qualified one of <see cref="EditFrame.Update"/>, not an order of column names.
/// </summary>
public sealed class MutationSemantics : IDisposable
{
    private MutationSemantics(
        TableDefinition table,
        string tableName,
        ColumnSemantics[] columns,
        BoundRule? tableRule,
        BoundCheckConstraint[] checkConstraints,
        string? checkConstraintsNotAdmitted,
        IndexCheck[] indexes)
    {
        Table = table;
        TableName = tableName;
        Columns = columns;
        TableRule = tableRule;
        CheckConstraints = checkConstraints;
        CheckConstraintsNotAdmitted = checkConstraintsNotAdmitted;
        Indexes = indexes;
        AutoNumber = Array.Find(columns, c => c.Column.IsAutoNumber)?.Column;
    }

    public TableDefinition Table { get; }

    public string TableName { get; }

    /// <summary>One entry per column, in <see cref="TableDefinition.Columns"/> order (the declaration order the paired-failure rule uses).</summary>
    public IReadOnlyList<ColumnSemantics> Columns { get; }

    public BoundRule? TableRule { get; }

    /// <summary>Table-level SQL <c>CHECK</c> constraints loaded from the table's <c>CheckConstraints</c> property.</summary>
    public IReadOnlyList<BoundCheckConstraint> CheckConstraints { get; }

    /// <summary>Non-null when the table has a <c>CheckConstraints</c> property whose payload could not be decoded or parsed.</summary>
    public string? CheckConstraintsNotAdmitted { get; }

    /// <summary>Whether the table declares any SQL <c>CHECK</c> constraint (bound, unbound, or malformed).</summary>
    public bool HasCheckConstraints => CheckConstraints.Count > 0 || CheckConstraintsNotAdmitted is not null;

    public IReadOnlyList<IndexCheck> Indexes { get; }

    /// <summary>The table's incrementing AutoNumber column, if it has one.</summary>
    public ColumnDescriptor? AutoNumber { get; }

    /// <summary>
    /// Compile from the table definition, its MR2 property blob (II-06 §5; v8qw's <see cref="PropertyBlob"/>) and the
    /// expression service's binder. A blob without a column's map leaves that column without stored properties.
    /// </summary>
    public static MutationSemantics Compile(string tableName, TableDefinition table, PropertyBlob? properties, IMutationRuleBinder binder, IndexDescriptorRules indexRules)
    {
        var columns = new ColumnSemantics[table.Columns.Count];
        for (int i = 0; i < columns.Length; i++)
        {
            ColumnDescriptor column = table.Columns[i];
            PropertyMap? map = properties?.Map(PropertySectionKind.ColumnValues, column.Name);
            string? defaultText = Text(map, "DefaultValue");
            IFieldDefault? @default = null;
            string? defaultNotAdmitted = null;
            if (!string.IsNullOrEmpty(defaultText))
            {
                @default = binder.BindDefault(table, column, defaultText, out defaultNotAdmitted);
            }
            string? ruleText = Text(map, "ValidationRule");
            BoundRule? rule = null;
            if (!string.IsNullOrEmpty(ruleText?.TrimEnd('\0')))
            {
                IMutationRule? bound = binder.BindFieldRule(table, column, ruleText, out string? notAdmitted);
                rule = new BoundRule(ruleText, NonEmpty(Text(map, "ValidationText")), bound, notAdmitted);
            }
            columns[i] = new ColumnSemantics
            {
                Column = column,
                QualifiedName = tableName + "." + column.Name,
                Required = Boolean(map, "Required") ?? false,
                AllowZeroLength = Boolean(map, "AllowZeroLength"),
                DefaultText = defaultText,
                Default = @default,
                DefaultNotAdmitted = defaultNotAdmitted,
                Rule = rule,
            };
        }
        PropertyMap? own = properties?.ObjectMap;
        string? tableRuleText = Text(own, "ValidationRule");
        BoundRule? tableRule = null;
        if (!string.IsNullOrEmpty(tableRuleText?.TrimEnd('\0')))
        {
            IMutationRule? bound = binder.BindTableRule(table, tableRuleText, out string? notAdmitted);
            tableRule = new BoundRule(tableRuleText, NonEmpty(Text(own, "ValidationText")), bound, notAdmitted);
        }
        (BoundCheckConstraint[] checkConstraints, string? checkConstraintsNotAdmitted) = CompileCheckConstraints(tableName, table, own, binder);
        var indexes = new List<IndexCheck>();
        foreach (LogicalIndex logical in table.LogicalIndexes)
        {
            if (table.PhysicalOf(logical) is not { } physical)
            {
                continue;
            }
            bool unique = physical.Unique(indexRules);
            bool refusesNull = physical.Required(indexRules);
            var keyColumns = new List<ColumnDescriptor>();
            foreach (IndexColumn ic in physical.Columns)
            {
                if (table.Columns.FirstOrDefault(c => c.PhysicalNumber == ic.PhysicalNumber) is { } c)
                {
                    keyColumns.Add(c);
                }
            }
            // Two logical indexes over one tree check once.
            if (!indexes.Exists(x => x.Physical.Number == physical.Number))
            {
                indexes.Add(new IndexCheck(logical, physical, keyColumns, unique, refusesNull));
            }
        }
        return new MutationSemantics(table, tableName, columns, tableRule, checkConstraints, checkConstraintsNotAdmitted, [.. indexes]);
    }

    private static (BoundCheckConstraint[] Constraints, string? NotAdmitted) CompileCheckConstraints(
        string tableName,
        TableDefinition table,
        PropertyMap? own,
        IMutationRuleBinder binder)
    {
        if (own is null)
        {
            return ([], null);
        }
        StoredProperty[] matches = [.. own.Properties.Where(p => string.Equals(p.Name, "CheckConstraints", StringComparison.OrdinalIgnoreCase))];
        if (matches.Length == 0)
        {
            return ([], null);
        }
        if (matches.Length > 1)
        {
            return ([], $"table '{tableName}' has {matches.Length} CheckConstraints properties; only a single CheckConstraints property is recorded");
        }
        PropertyValue decoded = PropertyBlob.Decode(matches[0]);
        if (decoded.Failure is not null || decoded.Text is null)
        {
            return ([], $"CheckConstraints property on '{tableName}' could not be decoded ({decoded.Failure ?? $"type 0x{matches[0].NativeType:X2} has no text payload"})");
        }
        if (!TryParseCheckConstraints(decoded.Text, out List<(string Name, string Expression)> pairs, out string? parseError))
        {
            return ([], $"CheckConstraints property on '{tableName}' has an unrecorded format: {parseError}");
        }
        var bound = new BoundCheckConstraint[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            (string name, string expr) = pairs[i];
            if (!TryMatchSingleColumnComparison(table, expr, out ColumnDescriptor? column, out string? reason))
            {
                bound[i] = new BoundCheckConstraint(name, expr, null, null, reason);
                continue;
            }
            IMutationRule? rule = binder.BindFieldRule(table, column!, expr, out string? notAdmitted);
            bound[i] = new BoundCheckConstraint(name, expr, column, rule, rule is null ? (notAdmitted ?? "expression binder did not admit the CHECK constraint") : null);
        }
        return (bound, null);
    }

    /// <summary>
    /// Parses the MR2 <c>CheckConstraints</c> memo payload: NUL-separated <c>(name, expression)</c> pairs terminated by
    /// an extra NUL (<c>"CK_Req_V\0V &gt;= 0\0\0"</c> in <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>,
    /// <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>).
    /// </summary>
    internal static bool TryParseCheckConstraints(string payload, out List<(string Name, string Expression)> pairs, out string? error)
    {
        pairs = [];
        if (payload.Length < 4 || !payload.EndsWith("\0\0", StringComparison.Ordinal))
        {
            error = "payload is not double-NUL terminated";
            return false;
        }
        string[] tokens = payload[..^2].Split('\0');
        if (tokens.Length == 0 || (tokens.Length & 1) != 0)
        {
            error = $"expected non-empty (name, expression) pairs, found {tokens.Length} NUL-delimited tokens";
            return false;
        }
        for (int i = 0; i < tokens.Length; i += 2)
        {
            string name = tokens[i];
            string expr = tokens[i + 1];
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(expr))
            {
                error = $"pair {i / 2} has an empty constraint name or expression";
                return false;
            }
            pairs.Add((name, expr));
        }
        error = null;
        return true;
    }

    private enum CheckTokenKind : byte { Ident, Literal, BoolLiteral, Op }

    private readonly record struct CheckToken(CheckTokenKind Kind, string Text);

    private static readonly HashSet<string> DisallowedCheckKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "EXISTS", "IN", "BETWEEN", "LIKE", "IS", "NULL", "AND", "OR", "NOT", "XOR", "EQV", "IMP", "MOD",
    };

    /// <summary>
    /// Admits only a single-column scalar comparison against a literal on one column of <paramref name="table"/>
    /// (<c>col op literal</c> or <c>literal op col</c>, with optional balanced outer parentheses), as recorded by
    /// <c>r-oledb-odbc/update-errors</c> (<c>V &gt;= 0</c>), <c>r-oledb-odbc/error-record-channels</c> (<c>V &gt;= 0</c>),
    /// and <c>r-q-cat-txn-lock/ii11-ado-check-constraint</c> (<c>V &gt; 0</c>, <c>V &lt; 20</c>).
    /// Rejects subqueries, cross-table references, multi-column expressions, compound predicates, and function calls so
    /// they stay refused by name.
    /// </summary>
    internal static bool TryMatchSingleColumnComparison(
        TableDefinition table,
        string expression,
        out ColumnDescriptor? column,
        out string? reason)
    {
        column = null;
        ReadOnlySpan<char> span = StripOuterParens(expression.AsSpan().Trim());
        if (span.IsEmpty)
        {
            reason = "CHECK expression is empty";
            return false;
        }
        var tokens = new List<CheckToken>(4);
        int i = 0;
        while (i < span.Length)
        {
            char ch = span[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }
            if (tokens.Count >= 3)
            {
                reason = "only a single-column comparison against a literal is recorded";
                return false;
            }
            if (ch == '[')
            {
                int close = span[(i + 1)..].IndexOf(']');
                if (close <= 0)
                {
                    reason = "unterminated or empty bracketed identifier in CHECK expression";
                    return false;
                }
                string name = span.Slice(i + 1, close).ToString();
                tokens.Add(new CheckToken(CheckTokenKind.Ident, name));
                i += close + 2;
                continue;
            }
            if (ch is '\'' or '"')
            {
                char quote = ch;
                int start = i;
                i++;
                bool closed = false;
                while (i < span.Length)
                {
                    if (span[i] == quote)
                    {
                        if (i + 1 < span.Length && span[i + 1] == quote)
                        {
                            i += 2;
                            continue;
                        }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed)
                {
                    reason = "unterminated string literal in CHECK expression";
                    return false;
                }
                tokens.Add(new CheckToken(CheckTokenKind.Literal, span[start..i].ToString()));
                continue;
            }
            if (ch == '#')
            {
                int close = span[(i + 1)..].IndexOf('#');
                if (close <= 0)
                {
                    reason = "unterminated or empty date literal in CHECK expression";
                    return false;
                }
                tokens.Add(new CheckToken(CheckTokenKind.Literal, span.Slice(i, close + 2).ToString()));
                i += close + 2;
                continue;
            }
            if (ch is '=' or '<' or '>')
            {
                int start = i;
                i++;
                if (i < span.Length && ((ch == '<' && span[i] is '>' or '=') || (ch == '>' && span[i] == '=')))
                {
                    i++;
                }
                tokens.Add(new CheckToken(CheckTokenKind.Op, span[start..i].ToString()));
                continue;
            }
            bool unarySignAllowed = tokens.Count == 0 || tokens[^1].Kind == CheckTokenKind.Op;
            if (char.IsAsciiDigit(ch) || (unarySignAllowed && ch is '+' or '-' && i + 1 < span.Length && (char.IsAsciiDigit(span[i + 1]) || span[i + 1] == '.')))
            {
                int start = i;
                if (ch is '+' or '-')
                {
                    i++;
                }
                bool sawDigit = false;
                while (i < span.Length && char.IsAsciiDigit(span[i]))
                {
                    sawDigit = true;
                    i++;
                }
                if (i < span.Length && span[i] == '.')
                {
                    i++;
                    while (i < span.Length && char.IsAsciiDigit(span[i]))
                    {
                        sawDigit = true;
                        i++;
                    }
                }
                if (!sawDigit)
                {
                    reason = "malformed numeric literal in CHECK expression";
                    return false;
                }
                if (i < span.Length && span[i] is 'e' or 'E')
                {
                    int expPos = i + 1;
                    if (expPos < span.Length && span[expPos] is '+' or '-')
                    {
                        expPos++;
                    }
                    int expDigits = expPos;
                    while (expDigits < span.Length && char.IsAsciiDigit(span[expDigits]))
                    {
                        expDigits++;
                    }
                    if (expDigits == expPos)
                    {
                        reason = "malformed numeric exponent in CHECK expression";
                        return false;
                    }
                    i = expDigits;
                }
                tokens.Add(new CheckToken(CheckTokenKind.Literal, span[start..i].ToString()));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                i++;
                while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_'))
                {
                    i++;
                }
                string word = span[start..i].ToString();
                if (string.Equals(word, "True", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(word, "False", StringComparison.OrdinalIgnoreCase))
                {
                    tokens.Add(new CheckToken(CheckTokenKind.BoolLiteral, word));
                }
                else if (DisallowedCheckKeywords.Contains(word))
                {
                    reason = $"CHECK expression uses keyword '{word}'; only a single-column comparison against a literal is recorded";
                    return false;
                }
                else
                {
                    tokens.Add(new CheckToken(CheckTokenKind.Ident, word));
                }
                continue;
            }
            reason = ch switch
            {
                '.' => "qualified or cross-table reference in CHECK expression is not recorded",
                '(' or ')' => "subquery, function call, or grouped predicate in CHECK expression is not recorded",
                _ => $"character '{ch}' in CHECK expression: only a single-column comparison against a literal is recorded",
            };
            return false;
        }
        if (tokens.Count != 3 || tokens[1].Kind != CheckTokenKind.Op)
        {
            reason = "only a single-column comparison against a literal is recorded";
            return false;
        }
        string? columnName = (tokens[0].Kind, tokens[2].Kind) switch
        {
            (CheckTokenKind.Ident, CheckTokenKind.Literal or CheckTokenKind.BoolLiteral) => tokens[0].Text,
            (CheckTokenKind.Literal or CheckTokenKind.BoolLiteral, CheckTokenKind.Ident) => tokens[2].Text,
            _ => null,
        };
        if (columnName is null)
        {
            reason = "CHECK expression must compare one column of the table against a scalar literal";
            return false;
        }
        ColumnDescriptor[] matched = [.. table.Columns.Where(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase))];
        if (matched.Length != 1)
        {
            reason = matched.Length == 0
                ? $"CHECK expression references unknown or external identifier '{columnName}'"
                : $"CHECK expression column '{columnName}' is ambiguous";
            return false;
        }
        column = matched[0];
        reason = null;
        return true;
    }

    private static ReadOnlySpan<char> StripOuterParens(ReadOnlySpan<char> span)
    {
        while (span.Length >= 2 && span[0] == '(' && span[^1] == ')')
        {
            int depth = 0;
            bool wrapsWhole = true;
            for (int i = 0; i < span.Length - 1; i++)
            {
                char c = span[i];
                if (c is '\'' or '"')
                {
                    char quote = c;
                    i++;
                    while (i < span.Length - 1)
                    {
                        if (span[i] == quote)
                        {
                            if (i + 1 < span.Length - 1 && span[i + 1] == quote)
                            {
                                i += 2;
                                continue;
                            }
                            break;
                        }
                        i++;
                    }
                    continue;
                }
                if (c == '[')
                {
                    int close = span[(i + 1)..].IndexOf(']');
                    if (close < 0)
                    {
                        return span;
                    }
                    i += close + 1;
                    continue;
                }
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        wrapsWhole = false;
                        break;
                    }
                }
            }
            if (!wrapsWhole)
            {
                break;
            }
            span = span[1..^1].Trim();
        }
        return span;
    }

    private static string? NonEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static string? Text(PropertyMap? map, string name) =>
        map?.Properties.FirstOrDefault(p => p.Name == name) is { } p && PropertyBlob.Decode(p) is { Failure: null } v ? v.Text : null;

    private static bool? Boolean(PropertyMap? map, string name) =>
        map?.Properties.FirstOrDefault(p => p.Name == name) is { } p && PropertyBlob.Decode(p) is { Failure: null } v ? v.Boolean : null;

    public void Dispose()
    {
        foreach (ColumnSemantics c in Columns)
        {
            c.Default?.Dispose();
            c.Rule?.Rule?.Dispose();
        }
        TableRule?.Rule?.Dispose();
        foreach (BoundCheckConstraint c in CheckConstraints)
        {
            c.Rule?.Dispose();
        }
    }
}
