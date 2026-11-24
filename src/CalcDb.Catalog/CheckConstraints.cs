using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Storage;

namespace CalcDb.Catalog;

/// <summary>
/// A SQL <c>CHECK</c> constraint row as <c>MSysObjects</c> stores it (<c>Type = 9</c>, <c>ParentId = 0x0F000003</c>
/// [Relationships container], <c>Flags = 0x10000000</c>): <c>Name</c> is the constraint name, <c>ForeignName</c>
/// names the table (stored with a trailing NUL), and <c>Connect</c> holds the constraint expression text
/// (verified on retained fixtures <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>,
/// <c>r-oledb-odbc-accdb.accdb</c>, <c>r-oledb-odbc-mdb.mdb</c>; <c>r-oledb-odbc/create-v2-accdb</c>,
/// <c>r-oledb-odbc/create-v2-mdb</c>).
/// When a <c>Type = 9</c> row deviates from that recorded shape, <see cref="NotAdmitted"/> names the mismatch so
/// writes refuse by name rather than silently ignoring the row.
/// </summary>
public sealed record CheckConstraintCatalogEntry(
    int Id,
    int ParentId,
    string Name,
    string TableName,
    string Expression,
    int Flags,
    RowId Root,
    string? NotAdmitted = null);

/// <summary>Reads SQL <c>CHECK</c> constraint rows (<c>Type = 9</c>) from <c>MSysObjects</c>.</summary>
public static class CheckConstraintCatalog
{
    /// <summary><c>MSysObjects.Type</c> for a SQL <c>CHECK</c> constraint row.</summary>
    public const short ObjectType = 9;

    /// <summary>Recorded <c>MSysObjects.ParentId</c> (Relationships container) on all four retained CHECK fixtures.</summary>
    public const int RecordedParentId = 0x0F000003;

    /// <summary>Recorded <c>MSysObjects.Flags</c> on all four retained CHECK fixtures.</summary>
    public const int RecordedFlags = 0x10000000;

    /// <summary>
    /// Reads every <c>Type = 9</c> constraint row from <c>MSysObjects</c> in scan order, or returns null with a
    /// diagnostic if a required column is missing or unreadable. Any <c>Type = 9</c> row whose field kinds,
    /// <c>ParentId</c>, <c>Flags</c>, or non-empty <c>Name</c>/<c>ForeignName</c>/<c>Connect</c> deviate from the
    /// four retained fixtures is returned with <see cref="CheckConstraintCatalogEntry.NotAdmitted"/> populated so
    /// writes refuse by name.
    /// </summary>
    public static IReadOnlyList<CheckConstraintCatalogEntry>? Read(
        PageCache cache,
        FormatDescriptor d,
        CatalogSnapshot catalog,
        ulong pageCount,
        out string? failure)
    {
        failure = null;
        TableDefinition objectTable = catalog.ObjectTable;
        ColumnDescriptor? idCol = Find(objectTable, "Id", NativeTypes.Long, out failure);
        if (idCol is null)
        {
            return null;
        }
        ColumnDescriptor? parentCol = Find(objectTable, "ParentId", NativeTypes.Long, out failure);
        if (parentCol is null)
        {
            return null;
        }
        ColumnDescriptor? nameCol = Find(objectTable, "Name", NativeTypes.Text, out failure);
        if (nameCol is null)
        {
            return null;
        }
        ColumnDescriptor? typeCol = Find(objectTable, "Type", NativeTypes.Integer, out failure);
        if (typeCol is null)
        {
            return null;
        }
        ColumnDescriptor? flagsCol = Find(objectTable, "Flags", NativeTypes.Long, out failure);
        if (flagsCol is null)
        {
            return null;
        }
        ColumnDescriptor? connectCol = Find(objectTable, "Connect", NativeTypes.Memo, out failure);
        if (connectCol is null)
        {
            return null;
        }
        ColumnDescriptor? foreignNameCol = Find(objectTable, "ForeignName", NativeTypes.Text, out failure);
        if (foreignNameCol is null)
        {
            return null;
        }

        var constraints = new List<CheckConstraintCatalogEntry>();
        var scanFailures = new List<TraversalFailure>();
        foreach (RowView row in RowResolver.Scan(cache, d, objectTable, pageCount, scanFailures))
        {
            using (row)
            {
                NativeValue type = FieldDecoder.Read(row, objectTable, typeCol, cache, d);
                if (type.Kind == NativeKind.Failed)
                {
                    failure = $"object table row {row.Root} Type: {type.Failure}";
                    return null;
                }
                if (type.Kind != NativeKind.Integer || (short)type.Integer != ObjectType)
                {
                    continue;
                }
                NativeValue id = FieldDecoder.Read(row, objectTable, idCol, cache, d);
                NativeValue parent = FieldDecoder.Read(row, objectTable, parentCol, cache, d);
                NativeValue name = FieldDecoder.Read(row, objectTable, nameCol, cache, d);
                NativeValue flags = FieldDecoder.Read(row, objectTable, flagsCol, cache, d);
                NativeValue connect = FieldDecoder.Read(row, objectTable, connectCol, cache, d);
                NativeValue foreignName = FieldDecoder.Read(row, objectTable, foreignNameCol, cache, d);
                NativeValue[] fields = [id, parent, name, flags, connect, foreignName];
                int bad = Array.FindIndex(fields, v => v.Kind == NativeKind.Failed);
                if (bad >= 0)
                {
                    failure = $"check constraint row {row.Root}: {fields[bad].Failure}";
                    return null;
                }

                List<string>? reasons = null;
                int idVal = 0;
                if (id.Kind == NativeKind.Long)
                {
                    idVal = (int)id.Integer;
                }
                else
                {
                    (reasons ??= []).Add($"Id has kind {id.Kind}, expected Long");
                }

                int parentVal = 0;
                if (parent.Kind != NativeKind.Long)
                {
                    (reasons ??= []).Add($"ParentId has kind {parent.Kind}, expected Long");
                }
                else
                {
                    parentVal = (int)parent.Integer;
                    if (parentVal != RecordedParentId)
                    {
                        (reasons ??= []).Add($"ParentId 0x{unchecked((uint)parentVal):X8} is not recorded (expected 0x{RecordedParentId:X8})");
                    }
                }

                int flagsVal = 0;
                if (flags.Kind != NativeKind.Long)
                {
                    (reasons ??= []).Add($"Flags has kind {flags.Kind}, expected Long");
                }
                else
                {
                    flagsVal = (int)flags.Integer;
                    if (flagsVal != RecordedFlags)
                    {
                        (reasons ??= []).Add($"Flags 0x{unchecked((uint)flagsVal):X8} is not recorded (expected 0x{RecordedFlags:X8})");
                    }
                }

                string nameVal = "";
                if (name.Kind != NativeKind.Text || string.IsNullOrEmpty(name.Text))
                {
                    (reasons ??= []).Add($"Name is {(name.Kind == NativeKind.Null ? "Null" : name.Kind == NativeKind.Text ? "empty" : $"kind {name.Kind}")}");
                }
                else
                {
                    nameVal = name.Text;
                }

                string tableVal = "";
                if (foreignName.Kind != NativeKind.Text || string.IsNullOrEmpty(foreignName.Text?.TrimEnd('\0')))
                {
                    (reasons ??= []).Add($"ForeignName is {(foreignName.Kind == NativeKind.Null ? "Null" : foreignName.Kind == NativeKind.Text ? "empty" : $"kind {foreignName.Kind}")}");
                }
                else
                {
                    tableVal = foreignName.Text!.TrimEnd('\0');
                }

                string exprVal = "";
                if (connect.Kind != NativeKind.Text || string.IsNullOrEmpty(connect.Text?.TrimEnd('\0')))
                {
                    (reasons ??= []).Add($"Connect is {(connect.Kind == NativeKind.Null ? "Null" : connect.Kind == NativeKind.Text ? "empty" : $"kind {connect.Kind}")}");
                }
                else
                {
                    exprVal = connect.Text!.TrimEnd('\0');
                }

                string? notAdmitted = reasons is null
                    ? null
                    : $"MSysObjects Type 9 row {row.Root}{(string.IsNullOrEmpty(nameVal) ? "" : $" '{nameVal}'")}{(string.IsNullOrEmpty(tableVal) ? "" : $" on '{tableVal}'")}: {string.Join("; ", reasons)}";

                constraints.Add(new CheckConstraintCatalogEntry(
                    Id: idVal,
                    ParentId: parentVal,
                    Name: nameVal,
                    TableName: tableVal,
                    Expression: exprVal,
                    Flags: flagsVal,
                    Root: row.Root,
                    NotAdmitted: notAdmitted));
            }
        }
        if (scanFailures.Count > 0)
        {
            failure = "object table scan: " + string.Join("; ", scanFailures.Select(f => $"{f.At}: {f.Predicate}"));
            return null;
        }
        return constraints;
    }

    /// <summary>
    /// Projects the <c>MSysObjects</c> <c>Type = 9</c> rows belonging to <paramref name="tableName"/> (plus any
    /// unattributed <see cref="CheckConstraintCatalogEntry.NotAdmitted"/> row whose <c>ForeignName</c> was missing so
    /// no table can silently bypass it) into <see cref="CatalogCheckConstraint"/> records for
    /// <see cref="MutationSemantics.Compile"/>.
    /// </summary>
    public static IReadOnlyList<CatalogCheckConstraint> ForTable(
        IReadOnlyList<CheckConstraintCatalogEntry> entries,
        string tableName)
    {
        List<CatalogCheckConstraint>? matched = null;
        foreach (CheckConstraintCatalogEntry entry in entries)
        {
            bool belongs = !string.IsNullOrEmpty(entry.TableName)
                ? CatalogRelationshipContext.NamesEqual(entry.TableName, tableName)
                : entry.NotAdmitted is not null;
            if (belongs)
            {
                (matched ??= []).Add(new CatalogCheckConstraint(entry.Name, entry.Expression, entry.NotAdmitted, entry.Id));
            }
        }
        return matched is null ? [] : matched;
    }

    private static ColumnDescriptor? Find(TableDefinition table, string name, byte expectedType, out string? failure)
    {
        ColumnDescriptor[] matches = [.. table.Columns.Where(c => c.Name == name)];
        if (matches.Length != 1 || matches[0].TypeCode != expectedType)
        {
            failure = $"object table: field {name} (type {expectedType}) is {(matches.Length == 0 ? "missing" : matches.Length > 1 ? "ambiguous" : $"of type {matches[0].TypeCode}")}";
            return null;
        }
        failure = null;
        return matches[0];
    }
}

/// <summary>Normalized comparison operator for a single-column SQL <c>CHECK</c> constraint (column on the left).</summary>
public enum CheckComparisonOp : byte
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
}

/// <summary>Literal kind admitted for a single-column SQL <c>CHECK</c> constraint.</summary>
public enum CheckLiteralKind : byte
{
    Long,
    Text,
}

/// <summary>
/// Single-column SQL <c>CHECK</c> constraint expression parser shared by <c>DdlBinder</c> and
/// <see cref="SchemaWriter.AddCheckConstraint"/> (evidence/r-q34-ddl-sweep/alt-check-*, r-q30-sql-check/op-*,
/// r-q31-sql-check-2/lit-*).
/// Admits only the recorded shapes:
/// <list type="bullet">
/// <item>A bare <c>Long</c> column on the left compared via <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>, <c>&lt;=</c>,
/// <c>&gt;</c>, or <c>&gt;=</c> against a signed 32-bit integer literal.</item>
/// <item>A non-negative 32-bit integer literal on the left compared via <c>=</c>, <c>&lt;&gt;</c>, <c>&lt;</c>,
/// <c>&lt;=</c>, <c>&gt;</c>, or <c>&gt;=</c> against a bare <c>Long</c> column, or a negative 32-bit integer literal
/// on the left compared via <c>&lt;=</c> (<c>r-q31-sql-check-2/lit-lhs-neg-le-sat</c>).</item>
/// <item>A bare <c>Text</c> column on the left compared via <c>&lt;&gt;</c> against a single-quoted Latin-1 string
/// literal with no leading ASCII space and no non-space/non-ASCII whitespace (<c>r-q30-sql-check/op-text-ne-sat</c>,
/// <c>r-q31-sql-check-2/lit-text-*</c>).</item>
/// </list>
/// Refuses every other shape by name.
/// </summary>
public sealed record CheckConstraintExpression(
    string Expression,
    string ColumnName,
    CheckComparisonOp Op,
    CheckLiteralKind LiteralKind,
    int LongValue = 0,
    string? TextValue = null)
{
    private static readonly HashSet<string> DisallowedKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "EXISTS", "IN", "BETWEEN", "LIKE", "IS", "NULL", "AND", "OR", "NOT", "XOR", "EQV", "IMP", "MOD", "TRUE", "FALSE",
    };

    /// <summary>Parse a recorded single-column CHECK expression, or return false with a named refusal reason.</summary>
    public static bool TryParse(string expression, out CheckConstraintExpression? parsed, out string? reason)
    {
        parsed = null;
        ReadOnlySpan<char> span = expression.AsSpan().Trim();
        if (span.IsEmpty)
        {
            reason = "CHECK expression is empty";
            return false;
        }
        foreach (char c in span)
        {
            if (c == '\0' || c > '\u00FF')
            {
                reason = $"CHECK expression contains non-Latin-1 or NUL character U+{(int)c:X4} (MSysObjects.Connect uses Latin-1 compressed UTF-16LE)";
                return false;
            }
        }
        string trimmed = span.ToString();

        // Case 1: signed 32-bit integer literal on the left, comparison op, bare column on the right.
        if (span[0] == '-' || char.IsAsciiDigit(span[0]))
        {
            bool isNegative = span[0] == '-';
            int i = 0;
            if (!TryReadSignedInt32(span, ref i, out int lhsInt, out reason))
            {
                return false;
            }
            SkipWhitespace(span, ref i);
            if (!TryReadComparisonOp(span, ref i, out CheckComparisonOp lhsOp, out reason))
            {
                return false;
            }
            if (isNegative && lhsOp != CheckComparisonOp.Le)
            {
                reason = $"CHECK operator '{OpText(lhsOp)}' with a negative integer literal on the left is not recorded (only '<=' is recorded, r-q31-sql-check-2/lit-lhs-neg-le-sat)";
                return false;
            }
            SkipWhitespace(span, ref i);
            if (!TryReadBareIdentifier(span, ref i, out string rhsCol, out reason))
            {
                return false;
            }
            if (i != span.Length)
            {
                reason = "only a single-column comparison against a 32-bit integer or Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
                return false;
            }
            parsed = new CheckConstraintExpression(trimmed, rhsCol, Flip(lhsOp), CheckLiteralKind.Long, LongValue: lhsInt);
            reason = null;
            return true;
        }

        // Case 2: bare column on the left, comparison op, signed 32-bit integer or single-quoted string literal on the right.
        if (!(char.IsLetter(span[0]) || span[0] == '_'))
        {
            reason = "only a bare column compared against a signed 32-bit integer or single-quoted Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
            return false;
        }
        int pos = 0;
        if (!TryReadBareIdentifier(span, ref pos, out string lhsCol, out reason))
        {
            return false;
        }
        SkipWhitespace(span, ref pos);
        if (!TryReadComparisonOp(span, ref pos, out CheckComparisonOp op, out reason))
        {
            return false;
        }
        SkipWhitespace(span, ref pos);
        if (pos >= span.Length)
        {
            reason = "CHECK expression is missing its right-hand literal";
            return false;
        }

        if (span[pos] == '\'')
        {
            if (op != CheckComparisonOp.Ne)
            {
                reason = $"CHECK operator '{OpText(op)}' on a Text column is not recorded (only '<>' is recorded, r-q30-sql-check/op-text-ne-sat, r-q31-sql-check-2/lit-text-empty-sat)";
                return false;
            }
            if (!TryReadStringLiteral(span, ref pos, out string lit, out reason))
            {
                return false;
            }
            if (pos != span.Length)
            {
                reason = "only a single-column comparison against a 32-bit integer or Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
                return false;
            }
            if (lit.Length > 0 && lit[0] == ' ')
            {
                reason = "leading spaces in a Text CHECK literal are not recorded (only trailing ASCII spaces are recorded, r-q31-sql-check-2/lit-text-trail-lit)";
                return false;
            }
            foreach (char ch in lit)
            {
                if (ch != ' ' && char.IsWhiteSpace(ch))
                {
                    reason = $"non-ASCII or control whitespace U+{(int)ch:X4} in a Text CHECK literal is not recorded (only trailing ASCII spaces are recorded, r-q31-sql-check-2/lit-text-trail-lit)";
                    return false;
                }
            }
            parsed = new CheckConstraintExpression(trimmed, lhsCol, op, CheckLiteralKind.Text, TextValue: lit);
            reason = null;
            return true;
        }

        if (span[pos] == '-' || char.IsAsciiDigit(span[pos]))
        {
            if (!TryReadSignedInt32(span, ref pos, out int rhsInt, out reason))
            {
                return false;
            }
            if (pos != span.Length)
            {
                reason = "only a single-column comparison against a 32-bit integer or Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
                return false;
            }
            parsed = new CheckConstraintExpression(trimmed, lhsCol, op, CheckLiteralKind.Long, LongValue: rhsInt);
            reason = null;
            return true;
        }

        reason = "only a bare column compared against a signed 32-bit integer or single-quoted Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
        return false;
    }

    private static string OpText(CheckComparisonOp op) => op switch
    {
        CheckComparisonOp.Eq => "=",
        CheckComparisonOp.Ne => "<>",
        CheckComparisonOp.Lt => "<",
        CheckComparisonOp.Le => "<=",
        CheckComparisonOp.Gt => ">",
        CheckComparisonOp.Ge => ">=",
        _ => op.ToString(),
    };

    private static CheckComparisonOp Flip(CheckComparisonOp op) => op switch
    {
        CheckComparisonOp.Lt => CheckComparisonOp.Gt,
        CheckComparisonOp.Le => CheckComparisonOp.Ge,
        CheckComparisonOp.Gt => CheckComparisonOp.Lt,
        CheckComparisonOp.Ge => CheckComparisonOp.Le,
        _ => op,
    };

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int i)
    {
        while (i < span.Length && char.IsWhiteSpace(span[i]))
        {
            i++;
        }
    }

    private static bool TryReadBareIdentifier(ReadOnlySpan<char> span, ref int i, out string name, out string? reason)
    {
        name = "";
        int start = i;
        if (i >= span.Length || !(char.IsLetter(span[i]) || span[i] == '_'))
        {
            reason = "expected a bare column identifier in CHECK expression";
            return false;
        }
        i++;
        while (i < span.Length && (char.IsLetterOrDigit(span[i]) || span[i] == '_'))
        {
            i++;
        }
        name = span[start..i].ToString();
        if (DisallowedKeywords.Contains(name))
        {
            reason = $"SQL keyword '{name}' in CHECK expression is not a recorded bare column comparison";
            return false;
        }
        SkipWhitespace(span, ref i);
        reason = null;
        return true;
    }

    private static bool TryReadComparisonOp(ReadOnlySpan<char> span, ref int i, out CheckComparisonOp op, out string? reason)
    {
        op = default;
        if (i >= span.Length)
        {
            reason = "expected a comparison operator (=, <>, <, <=, >, >=) in CHECK expression";
            return false;
        }
        if (i + 1 < span.Length)
        {
            ReadOnlySpan<char> two = span.Slice(i, 2);
            if (two.SequenceEqual("<>")) { i += 2; op = CheckComparisonOp.Ne; reason = null; return true; }
            if (two.SequenceEqual("<=")) { i += 2; op = CheckComparisonOp.Le; reason = null; return true; }
            if (two.SequenceEqual(">=")) { i += 2; op = CheckComparisonOp.Ge; reason = null; return true; }
        }
        switch (span[i])
        {
            case '=': i++; op = CheckComparisonOp.Eq; reason = null; return true;
            case '<': i++; op = CheckComparisonOp.Lt; reason = null; return true;
            case '>': i++; op = CheckComparisonOp.Gt; reason = null; return true;
            default:
                reason = "expected a comparison operator (=, <>, <, <=, >, >=) in CHECK expression";
                return false;
        }
    }

    private static bool TryReadSignedInt32(ReadOnlySpan<char> span, ref int i, out int value, out string? reason)
    {
        value = 0;
        int start = i;
        if (i < span.Length && span[i] == '-')
        {
            i++;
        }
        int digits = i;
        while (i < span.Length && char.IsAsciiDigit(span[i]))
        {
            i++;
        }
        if (i == digits || (i < span.Length && span[i] == '.'))
        {
            reason = "only signed 32-bit integer literals are recorded in numeric CHECK expressions";
            return false;
        }
        if (!int.TryParse(span[start..i], System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            reason = "integer literal in CHECK expression overflows signed 32-bit int";
            return false;
        }
        SkipWhitespace(span, ref i);
        reason = null;
        return true;
    }

    private static bool TryReadStringLiteral(ReadOnlySpan<char> span, ref int i, out string value, out string? reason)
    {
        value = "";
        if (i >= span.Length || span[i] != '\'')
        {
            reason = "expected a single-quoted string literal in CHECK expression";
            return false;
        }
        i++;
        var sb = new System.Text.StringBuilder();
        while (i < span.Length)
        {
            if (span[i] == '\'')
            {
                if (i + 1 < span.Length && span[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }
                i++;
                SkipWhitespace(span, ref i);
                value = sb.ToString();
                reason = null;
                return true;
            }
            sb.Append(span[i]);
            i++;
        }
        reason = "unterminated string literal in CHECK expression";
        return false;
    }
}


