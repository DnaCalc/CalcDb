using System.Globalization;
using System.Text;

namespace CalcDb.Isam.Edit;

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
/// Single-column SQL <c>CHECK</c> constraint expression parser shared by <c>CalcDb.Isam</c>, <c>CalcDb.Catalog</c>,
/// and <c>CalcDb.Sql</c> (<c>evidence/r-q34-ddl-sweep/alt-check-*</c>, <c>evidence/r-q30-sql-check/op-*</c>,
/// <c>evidence/r-q31-sql-check-2/lit-*</c>).
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
            isNegative = isNegative || lhsInt < 0;
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
            parsed = new CheckConstraintExpression(
                trimmed,
                rhsCol,
                Flip(lhsOp),
                CheckLiteralKind.Long,
                LongValue: lhsInt);
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
                if (char.IsControl(ch) || (ch != ' ' && char.IsWhiteSpace(ch)))
                {
                    reason = $"non-ASCII or control whitespace/character U+{(int)ch:X4} in a Text CHECK literal is not recorded (only trailing ASCII spaces are recorded, r-q31-sql-check-2/lit-text-trail-lit)";
                    return false;
                }
            }
            parsed = new CheckConstraintExpression(
                trimmed,
                lhsCol,
                op,
                CheckLiteralKind.Text,
                TextValue: lit);
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
            parsed = new CheckConstraintExpression(
                trimmed,
                lhsCol,
                op,
                CheckLiteralKind.Long,
                LongValue: rhsInt);
            reason = null;
            return true;
        }

        reason = "only a bare column compared against a signed 32-bit integer or single-quoted Latin-1 string literal is recorded for ADD CONSTRAINT CHECK";
        return false;
    }

    internal static string OpText(CheckComparisonOp op) => op switch
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
        if (!int.TryParse(span[start..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
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
        var sb = new StringBuilder();
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

/// <summary>
/// Decoder and encoder for the MR2 table-level <c>CheckConstraints</c> LvProp memo payload: NUL-separated UTF-16LE
/// <c>(name, expression)</c> pairs terminated by an extra NUL (<c>"CK_Req_V\0V &gt;= 0\0\0"</c> in
/// <c>r-oledb-odbc-v2-accdb.accdb</c>, <c>r-oledb-odbc-v2-mdb.mdb</c>, <c>r-oledb-odbc-accdb.accdb</c>,
/// <c>r-oledb-odbc-mdb.mdb</c>, and <c>evidence/r-q34-ddl-sweep/alt-check-*</c>).
/// Shared by <see cref="MutationSemantics"/> and <c>CalcDb.Catalog.SchemaWriter</c>.
/// </summary>
public static class CheckConstraintsPayload
{
    /// <summary>Decode raw uncompressed UTF-16LE <c>CheckConstraints</c> property bytes into <c>(Name, Expression)</c> pairs.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> utf16LePayload, out List<(string Name, string Expression)> pairs, out string? error)
    {
        pairs = [];
        if (utf16LePayload.Length < 4 || (utf16LePayload.Length & 1) != 0)
        {
            error = $"payload length {utf16LePayload.Length} is not an even UTF-16LE byte count >= 4";
            return false;
        }
        return TryDecode(Encoding.Unicode.GetString(utf16LePayload), out pairs, out error);
    }

    /// <summary>Decode an already-decoded <c>CheckConstraints</c> text payload into <c>(Name, Expression)</c> pairs.</summary>
    public static bool TryDecode(string payload, out List<(string Name, string Expression)> pairs, out string? error)
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
        pairs = new List<(string Name, string Expression)>(tokens.Length / 2);
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

    /// <summary>Encode <c>(Name, Expression)</c> pairs into the double-NUL-terminated UTF-16LE <c>CheckConstraints</c> payload.</summary>
    public static byte[] Encode(IEnumerable<(string Name, string Expression)> pairs)
    {
        string joined = string.Join('\0', pairs.SelectMany(pair => new[] { pair.Name, pair.Expression })) + "\0\0";
        return Encoding.Unicode.GetBytes(joined);
    }
}
