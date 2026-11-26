using System.Globalization;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Com.Ado;

internal enum AdoFilterOp : byte
{
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Like,
}

internal enum AdoFilterLiteralKind : byte
{
    Null,
    QuotedText,
    DoubleQuotedText,
    Date,
    Number,
    BoolTrue,
}

internal enum AdoLikeMode : byte
{
    Exact,
    Prefix,
    PrefixAtLeastOneMore,
    Contains,
}

internal readonly record struct AdoFilterLiteral(
    AdoFilterLiteralKind Kind,
    string Text,
    double OaDate,
    AdoLikeMode LikeMode = AdoLikeMode.Exact,
    string LikeNeedle = "");

internal readonly record struct AdoFilterPredicate(
    string ColumnName,
    int ColumnOrdinal,
    AdoFilterOp Op,
    AdoFilterLiteral Literal);

/// <summary>
/// Parsed ADO Recordset.Filter / Recordset.Find criteria expression
/// (evidence/r-ado-1b/view-filter-operators, view-filter-literals, view-filter-constants,
/// evidence/r-q-ado-misc/view-like-percent-sort-clear, evidence/r-ado-1b/view-find-client,
/// view-sort-server, evidence/r-str-ado-act/ado-view).
/// </summary>
internal sealed unsafe class AdoFilterExpression
{
    private readonly List<List<AdoFilterPredicate>> _orBranches;

    private AdoFilterExpression(List<List<AdoFilterPredicate>> orBranches) => _orBranches = orBranches;

    public static AdoFilterExpression? Parse(
        string criteria,
        AdoFieldDescriptor[] columns,
        bool singleClauseOnly,
        out AdoError? adoError,
        out Failure? refusal)
    {
        adoError = null;
        refusal = null;
        var parser = new FilterParser(criteria, singleClauseOnly);
        List<List<AdoFilterPredicate>>? raw = parser.ParseAll(out adoError, out refusal);
        if (raw is null)
        {
            return null;
        }

        // Resolve column names ONLY after full syntax validation succeeds
        // (view-filter-operators: "T.S = 'alpha'" -> 0x800A0CC1; "UPPER(S) = 'ALPHA'" -> 0x800A0BB9).
        for (int b = 0; b < raw.Count; b++)
        {
            List<AdoFilterPredicate> branch = raw[b];
            for (int p = 0; p < branch.Count; p++)
            {
                AdoFilterPredicate pred = branch[p];
                int ordinal = FindColumnOrdinal(columns, pred.ColumnName);
                if (ordinal < 0)
                {
                    adoError = AdoErrors.ItemNotFound;
                    return null;
                }
                branch[p] = pred with { ColumnOrdinal = ordinal };
            }
        }
        return new AdoFilterExpression(raw);
    }

    public bool MatchesRowSource(RecordsetObject rs, out Failure? failure)
    {
        failure = null;
        foreach (List<AdoFilterPredicate> andClause in _orBranches)
        {
            bool branchMatch = true;
            foreach (AdoFilterPredicate pred in andClause)
            {
                OwnedVariant? cell = rs.ReadCurrentRowCell(pred.ColumnOrdinal, out failure);
                if (cell is null)
                {
                    return false;
                }
                using (cell)
                {
                    if (!EvaluatePredicate(cell, pred, out failure))
                    {
                        if (failure is not null)
                        {
                            return false;
                        }
                        branchMatch = false;
                        break;
                    }
                }
            }
            if (branchMatch)
            {
                return true;
            }
        }
        return false;
    }

    public bool MatchesSnapshotRow(RecordsetObject rs, MemoryRowSource mem, int rowIndex, out Failure? failure)
    {
        failure = null;
        foreach (List<AdoFilterPredicate> andClause in _orBranches)
        {
            bool branchMatch = true;
            foreach (AdoFilterPredicate pred in andClause)
            {
                OwnedVariant? cell = rs.ReadSnapshotRowCell(mem, rowIndex, pred.ColumnOrdinal, out failure);
                if (cell is null)
                {
                    return false;
                }
                using (cell)
                {
                    if (!EvaluatePredicate(cell, pred, out failure))
                    {
                        if (failure is not null)
                        {
                            return false;
                        }
                        branchMatch = false;
                        break;
                    }
                }
            }
            if (branchMatch)
            {
                return true;
            }
        }
        return false;
    }

    public bool MatchesClientRow(ClientRow row, out Failure? failure)
    {
        failure = null;
        foreach (List<AdoFilterPredicate> andClause in _orBranches)
        {
            bool branchMatch = true;
            foreach (AdoFilterPredicate pred in andClause)
            {
                if (!EvaluatePredicate(row.Current[pred.ColumnOrdinal], pred, out failure))
                {
                    if (failure is not null)
                    {
                        return false;
                    }
                    branchMatch = false;
                    break;
                }
            }
            if (branchMatch)
            {
                return true;
            }
        }
        return false;
    }

    private static bool EvaluatePredicate(OwnedVariant cell, in AdoFilterPredicate pred, out Failure? failure)
    {
        failure = null;
        NativeVariant* v = cell.Pointer;
        VarType vt = (VarType)v->Vt;
        bool isNull = vt is VarType.Null or VarType.Empty;

        if (pred.Literal.Kind == AdoFilterLiteralKind.Null)
        {
            switch (pred.Op)
            {
                case AdoFilterOp.Eq:
                    return isNull;
                case AdoFilterOp.Ne:
                    return !isNull;
                default:
                    failure = Failure.NotImplemented($"Recordset.Filter/Find ordering comparison '{pred.Op}' against NULL (not recorded)");
                    return false;
            }
        }
        if (isNull)
        {
            return false;
        }

        if (pred.Op == AdoFilterOp.Like)
        {
            if (vt != VarType.Bstr)
            {
                failure = Failure.NotImplemented($"Recordset.Filter/Find LIKE on non-text column ({vt}) (not recorded)");
                return false;
            }
            ReadOnlySpan<char> span = v->Pointer == 0 ? ReadOnlySpan<char>.Empty : Bstrs.Chars(v->Pointer);
            ReadOnlySpan<char> needle = pred.Literal.LikeNeedle.AsSpan();
            switch (pred.Literal.LikeMode)
            {
                case AdoLikeMode.Exact:
                    return span.Equals(needle, StringComparison.OrdinalIgnoreCase);
                case AdoLikeMode.Prefix:
                    return span.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                case AdoLikeMode.PrefixAtLeastOneMore:
                    // evidence/r-q-ado-misc/view-like-percent-sort-clear (#107-108): 'alph_' matches 'alpha' and 'alpha2'
                    // (both > 4 chars), and does not distinguish whether an exact-length 'alph' row matches; refuse that case.
                    if (span.Equals(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find LIKE trailing '_' against exact-prefix value '{pred.Literal.LikeNeedle}' (not distinguished by evidence/r-q-ado-misc/view-like-percent-sort-clear)");
                        return false;
                    }
                    return span.Length > needle.Length && span.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
                case AdoLikeMode.Contains:
                    return span.Contains(needle, StringComparison.OrdinalIgnoreCase);
                default:
                    failure = Failure.NotImplemented($"Recordset.Filter/Find LIKE mode {pred.Literal.LikeMode} (not recorded)");
                    return false;
            }
        }

        if (!TryCompareCellToLiteral(cell, pred.Op, pred.Literal, out int cmp, out failure))
        {
            return false;
        }
        return pred.Op switch
        {
            AdoFilterOp.Eq => cmp == 0,
            AdoFilterOp.Ne => cmp != 0,
            AdoFilterOp.Lt => cmp < 0,
            AdoFilterOp.Le => cmp <= 0,
            AdoFilterOp.Gt => cmp > 0,
            AdoFilterOp.Ge => cmp >= 0,
            _ => false,
        };
    }

    private static bool TryCompareCellToLiteral(
        OwnedVariant cell,
        AdoFilterOp op,
        in AdoFilterLiteral lit,
        out int cmp,
        out Failure? failure)
    {
        cmp = 0;
        failure = null;
        NativeVariant* v = cell.Pointer;
        VarType vt = (VarType)v->Vt;
        switch (vt)
        {
            case VarType.Bstr:
                {
                    if (lit.Kind is not (AdoFilterLiteralKind.QuotedText or AdoFilterLiteralKind.DoubleQuotedText))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find comparing text column against {lit.Kind} literal '{lit.Text}' (not recorded)");
                        return false;
                    }
                    ReadOnlySpan<char> span = v->Pointer == 0 ? ReadOnlySpan<char>.Empty : Bstrs.Chars(v->Pointer);
                    cmp = span.CompareTo(lit.Text.AsSpan(), StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            case VarType.Bool:
                {
                    if (op is not (AdoFilterOp.Eq or AdoFilterOp.Ne))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find ordering comparison '{op}' on Boolean column (not recorded)");
                        return false;
                    }
                    // evidence/r-ado-1b/view-filter-literals (#270, #289, #308): only True, 1, -1 are recorded for Boolean filters.
                    bool isRecordedTrue = lit.Kind == AdoFilterLiteralKind.BoolTrue
                        || (lit.Kind == AdoFilterLiteralKind.Number && (lit.Text == "1" || lit.Text == "-1"));
                    if (!isRecordedTrue)
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find Boolean comparison against '{lit.Text}' (only True, 1, -1 are recorded)");
                        return false;
                    }
                    bool cellBool = (short)v->Int64 != 0;
                    cmp = cellBool ? 0 : -1;
                    return true;
                }
            case VarType.Date:
                {
                    double target;
                    if (lit.Kind == AdoFilterLiteralKind.Date)
                    {
                        target = lit.OaDate;
                    }
                    else if (lit.Kind == AdoFilterLiteralKind.QuotedText && TryParseSharedDate(lit.Text.AsSpan(), out target))
                    {
                        // Quoted date text parsed via CalcDb.Values.Scalar.ScalarLiterals.TryDate.
                    }
                    else
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find unparseable Date literal '{lit.Text}' (not recorded)");
                        return false;
                    }
                    cmp = v->Double.CompareTo(target);
                    return true;
                }
            case VarType.R4:
                {
                    if (!double.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find unparseable numeric literal '{lit.Text}' (not recorded)");
                        return false;
                    }
                    float f = BitConverter.Int32BitsToSingle((int)v->Int64);
                    cmp = ((double)f).CompareTo(d);
                    return true;
                }
            case VarType.R8:
                {
                    if (!double.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find unparseable numeric literal '{lit.Text}' (not recorded)");
                        return false;
                    }
                    cmp = v->Double.CompareTo(d);
                    return true;
                }
            case VarType.UI1:
            case VarType.I2:
            case VarType.I4:
            case VarType.I8:
            case VarType.Currency:
            case VarType.Decimal:
                {
                    if (!decimal.TryParse(lit.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal litDec))
                    {
                        failure = Failure.NotImplemented($"Recordset.Filter/Find unparseable numeric literal '{lit.Text}' (not recorded)");
                        return false;
                    }
                    cmp = CellAsDecimal(cell).CompareTo(litDec);
                    return true;
                }
            default:
                failure = Failure.NotImplemented($"Recordset.Filter/Find comparison on cell VarType {vt} (not recorded)");
                return false;
        }
    }

    internal static decimal CellAsDecimal(OwnedVariant cell)
    {
        NativeVariant* v = cell.Pointer;
        return (VarType)v->Vt switch
        {
            VarType.UI1 => (byte)v->Int64,
            VarType.I2 => (short)v->Int64,
            VarType.I4 => (int)v->Int64,
            VarType.I8 => v->Int64,
            VarType.Currency => decimal.FromOACurrency(v->Int64),
            VarType.Decimal => ToManagedDecimal(cell.AsDecimal()),
            _ => throw new InvalidOperationException($"CellAsDecimal called on non-numeric VarType {(VarType)v->Vt}"),
        };
    }

    private static decimal ToManagedDecimal(DecimalValue d) =>
        new((int)(d.Low64 & 0xFFFF_FFFFUL), (int)(d.Low64 >> 32), (int)d.High32, d.Negative, d.Scale);

    internal static bool TryParseSharedDate(ReadOnlySpan<char> text, out double oaDate)
    {
        if (ScalarLiterals.TryDate(text.Trim(), out NativeVariant dateVal))
        {
            oaDate = dateVal.Double;
            return true;
        }
        oaDate = 0;
        return false;
    }

    internal static int FindColumnOrdinal(AdoFieldDescriptor[] columns, string name)
    {
        for (int i = 0; i < columns.Length; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private ref struct FilterParser(string source, bool singleClauseOnly)
    {
        private readonly string _src = source;
        private readonly bool _singleClauseOnly = singleClauseOnly;
        private int _pos = 0;

        public List<List<AdoFilterPredicate>>? ParseAll(out AdoError? adoError, out Failure? refusal)
        {
            adoError = null;
            refusal = null;
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return null;
            }

            var branches = new List<List<AdoFilterPredicate>>();
            while (true)
            {
                var andBranch = new List<AdoFilterPredicate>();
                while (true)
                {
                    if (!TryParsePredicate(out AdoFilterPredicate pred, out adoError, out refusal))
                    {
                        return null;
                    }
                    andBranch.Add(pred);
                    SkipWhitespace();
                    if (_singleClauseOnly)
                    {
                        break;
                    }
                    if (!TryConsumeKeyword("AND"))
                    {
                        break;
                    }
                }
                branches.Add(andBranch);
                SkipWhitespace();
                if (_singleClauseOnly)
                {
                    break;
                }
                if (!TryConsumeKeyword("OR"))
                {
                    break;
                }
            }

            SkipWhitespace();
            if (_pos < _src.Length)
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return null;
            }
            return branches;
        }

        private bool TryParsePredicate(out AdoFilterPredicate pred, out AdoError? adoError, out Failure? refusal)
        {
            pred = default;
            adoError = null;
            refusal = null;

            if (!TryReadColumnName(out string colName))
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }
            if (!TryReadOperator(out AdoFilterOp op))
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }
            if (!TryReadLiteral(op == AdoFilterOp.Like, out AdoFilterLiteral lit, out adoError, out refusal))
            {
                return false;
            }
            if (lit.Kind == AdoFilterLiteralKind.Null && op is not (AdoFilterOp.Eq or AdoFilterOp.Ne))
            {
                refusal = Failure.NotImplemented($"Recordset.Filter/Find ordering comparison '{op}' against NULL (not recorded)");
                return false;
            }
            pred = new AdoFilterPredicate(colName, -1, op, lit);
            return true;
        }

        private bool TryReadColumnName(out string name)
        {
            name = "";
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                return false;
            }
            if (_src[_pos] == '[')
            {
                _pos++;
                int start = _pos;
                int close = _src.IndexOf(']', start);
                if (close < 0 || close == start)
                {
                    return false;
                }
                name = _src[start..close];
                _pos = close + 1;
                return true;
            }
            int idStart = _pos;
            while (_pos < _src.Length && IsIdentifierChar(_src[_pos]))
            {
                _pos++;
            }
            if (_pos == idStart)
            {
                return false;
            }
            string candidate = _src[idStart.._pos];
            if (string.Equals(candidate, "NOT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, "AND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate, "OR", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            name = candidate;
            return true;
        }

        private bool TryReadOperator(out AdoFilterOp op)
        {
            op = default;
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                return false;
            }
            char c = _src[_pos];
            if (c == '=')
            {
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '=')
                {
                    return false; // "==" rejected 0x800A0BB9
                }
                op = AdoFilterOp.Eq;
                return true;
            }
            if (c == '<')
            {
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '>')
                {
                    _pos++;
                    op = AdoFilterOp.Ne;
                    return true;
                }
                if (_pos < _src.Length && _src[_pos] == '=')
                {
                    _pos++;
                    op = AdoFilterOp.Le;
                    return true;
                }
                op = AdoFilterOp.Lt;
                return true;
            }
            if (c == '>')
            {
                _pos++;
                if (_pos < _src.Length && _src[_pos] == '=')
                {
                    _pos++;
                    op = AdoFilterOp.Ge;
                    return true;
                }
                op = AdoFilterOp.Gt;
                return true;
            }
            if (TryConsumeKeyword("LIKE"))
            {
                op = AdoFilterOp.Like;
                return true;
            }
            return false;
        }

        private bool TryReadLiteral(bool isLike, out AdoFilterLiteral literal, out AdoError? adoError, out Failure? refusal)
        {
            literal = default;
            adoError = null;
            refusal = null;
            SkipWhitespace();
            if (_pos >= _src.Length)
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }

            char c = _src[_pos];
            if (c == '\'')
            {
                _pos++;
                var chars = new System.Text.StringBuilder();
                bool closed = false;
                while (_pos < _src.Length)
                {
                    char ch = _src[_pos++];
                    if (ch == '\'')
                    {
                        if (_pos < _src.Length && _src[_pos] == '\'')
                        {
                            chars.Append('\'');
                            _pos++;
                        }
                        else
                        {
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        chars.Append(ch);
                    }
                }
                if (!closed)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return false;
                }
                string text = chars.ToString();
                if (isLike)
                {
                    return TryParseLikePattern(text, out literal, out adoError, out refusal);
                }
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.QuotedText, text, 0);
                return true;
            }

            if (c == '#')
            {
                if (isLike)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return false;
                }
                _pos++;
                int start = _pos;
                int close = _src.IndexOf('#', start);
                if (close < 0)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return false;
                }
                ReadOnlySpan<char> inner = _src.AsSpan(start, close - start);
                _pos = close + 1;
                if (!TryParseSharedDate(inner, out double oaDate))
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return false;
                }
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.Date, inner.ToString(), oaDate);
                return true;
            }

            if (c == '"')
            {
                // evidence/r-ado-1b/view-filter-literals #155: S = "alpha" succeeds and returns 0 rows (quotes kept as literal text).
                if (isLike)
                {
                    refusal = Failure.NotImplemented("Recordset.Filter/Find LIKE with double-quoted pattern (not recorded)");
                    return false;
                }
                int start = _pos;
                _pos++;
                int close = _src.IndexOf('"', _pos);
                if (close < 0)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return false;
                }
                _pos = close + 1;
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.DoubleQuotedText, _src[start.._pos], 0);
                return true;
            }

            // Unquoted token: only NULL, True, and numeric literals are recorded.
            if (c is '=' or '<' or '>' or '(' or ')' or ',' or '!')
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }
            int tokStart = _pos;
            while (_pos < _src.Length && !char.IsWhiteSpace(_src[_pos]) && _src[_pos] is not ('(' or ')' or '=' or '<' or '>'))
            {
                _pos++;
            }
            if (_pos == tokStart)
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }
            string tok = _src[tokStart.._pos];
            if (string.Equals(tok, "AND", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tok, "OR", StringComparison.OrdinalIgnoreCase))
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }
            if (isLike)
            {
                refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with unquoted pattern '{tok}' (not recorded)");
                return false;
            }
            if (string.Equals(tok, "NULL", StringComparison.OrdinalIgnoreCase))
            {
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.Null, tok, 0);
                return true;
            }
            if (string.Equals(tok, "True", StringComparison.OrdinalIgnoreCase))
            {
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.BoolTrue, tok, 0);
                return true;
            }
            if (IsNumericLiteral(tok))
            {
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.Number, tok, 0);
                return true;
            }
            refusal = Failure.NotImplemented($"Recordset.Filter/Find with unquoted literal '{tok}' (not recorded)");
            return false;
        }

        private static bool IsNumericLiteral(string tok)
        {
            if (tok.Length == 0)
            {
                return false;
            }
            int i = 0;
            if (tok[0] is '+' or '-')
            {
                i++;
                if (i >= tok.Length)
                {
                    return false;
                }
            }
            bool hasDigit = false;
            bool hasDot = false;
            for (; i < tok.Length; i++)
            {
                char ch = tok[i];
                if (ch is >= '0' and <= '9')
                {
                    hasDigit = true;
                }
                else if (ch == '.' && !hasDot)
                {
                    hasDot = true;
                }
                else
                {
                    return false;
                }
            }
            return hasDigit;
        }

        private static bool TryParseLikePattern(string pattern, out AdoFilterLiteral literal, out AdoError? adoError, out Failure? refusal)
        {
            literal = default;
            adoError = null;
            refusal = null;

            bool leadWild = pattern.Length > 0 && pattern[0] is '*' or '%';
            bool trailWild = pattern.Length > 0 && pattern[^1] is '*' or '%';

            // evidence/r-ado-1b/view-filter-operators ("S LIKE '%a'"), evidence/r-q-ado-misc/view-like-percent-sort-clear ("T LIKE '*1'", "T LIKE '%1'"):
            // leading-only wildcard is refused 0x800A0BB9.
            if (leadWild && !trailWild)
            {
                adoError = AdoErrors.ArgumentsWrongType;
                return false;
            }

            if (leadWild && trailWild)
            {
                string inner = pattern.Length <= 2 ? "" : pattern[1..^1];
                if (inner.Length == 0)
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with wildcard-only pattern '{pattern}' (not recorded)");
                    return false;
                }
                if (HasInteriorWildcard(inner))
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with interior wildcard '{pattern}' (not recorded)");
                    return false;
                }
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.QuotedText, pattern, 0, AdoLikeMode.Contains, inner);
                return true;
            }

            if (trailWild)
            {
                string prefix = pattern[..^1];
                if (prefix.Length == 0)
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with wildcard-only pattern '{pattern}' (not recorded)");
                    return false;
                }
                if (HasInteriorWildcard(prefix))
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with interior wildcard '{pattern}' (not recorded)");
                    return false;
                }
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.QuotedText, pattern, 0, AdoLikeMode.Prefix, prefix);
                return true;
            }

            // evidence/r-q-ado-misc/view-like-percent-sort-clear (#107-108): "T LIKE 'alph_'" matches "alpha" and "alpha2"
            // (exact-prefix match is refused during evaluation because the record does not distinguish it),
            // whereas "T LIKE 'alph?'" treats '?' literally (0 matches).
            if (pattern.Length > 0 && pattern[^1] == '_')
            {
                string prefix = pattern[..^1];
                if (prefix.Length == 0)
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with wildcard-only pattern '{pattern}' (not recorded)");
                    return false;
                }
                if (HasInteriorWildcard(prefix))
                {
                    refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with interior wildcard '{pattern}' (not recorded)");
                    return false;
                }
                literal = new AdoFilterLiteral(AdoFilterLiteralKind.QuotedText, pattern, 0, AdoLikeMode.PrefixAtLeastOneMore, prefix);
                return true;
            }

            if (HasInteriorWildcard(pattern))
            {
                refusal = Failure.NotImplemented($"Recordset.Filter/Find LIKE with interior wildcard '{pattern}' (not recorded)");
                return false;
            }

            literal = new AdoFilterLiteral(AdoFilterLiteralKind.QuotedText, pattern, 0, AdoLikeMode.Exact, pattern);
            return true;
        }

        private static bool HasInteriorWildcard(string s) => s.IndexOfAny(['*', '%', '_']) >= 0;

        private bool TryConsumeKeyword(string keyword)
        {
            SkipWhitespace();
            int len = keyword.Length;
            if (_pos + len > _src.Length)
            {
                return false;
            }
            if (!_src.AsSpan(_pos, len).Equals(keyword.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (_pos + len < _src.Length && (char.IsLetterOrDigit(_src[_pos + len]) || _src[_pos + len] == '_'))
            {
                return false;
            }
            _pos += len;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos]))
            {
                _pos++;
            }
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';
    }
}

internal readonly record struct AdoSortKey(int ColumnOrdinal, bool Descending);

/// <summary>
/// Parsed ADO Recordset.Sort specification for open client cursors
/// (evidence/r-ado-1b/view-sort-grammar, evidence/r-q-ado-misc/view-like-percent-sort-clear, evidence/r-str-ado-act/ado-view).
/// </summary>
internal sealed unsafe class AdoSortSpec
{
    public AdoSortSpec(AdoSortKey[] keys) => Keys = keys;

    public AdoSortKey[] Keys { get; }

    public static AdoSortSpec? Parse(
        string sortText,
        AdoFieldDescriptor[] columns,
        out AdoError? adoError,
        out Failure? cursorEngineFailure)
    {
        adoError = null;
        cursorEngineFailure = null;

        if (sortText.Length == 0)
        {
            return new AdoSortSpec([]);
        }
        if (string.IsNullOrWhiteSpace(sortText))
        {
            cursorEngineFailure = Failure.NotImplemented("Recordset.Sort with whitespace-only string (not recorded)");
            return null;
        }

        var rawKeys = new List<(string Name, bool Descending)>();
        int pos = 0;
        int len = sortText.Length;
        while (pos < len)
        {
            while (pos < len && char.IsWhiteSpace(sortText[pos]))
            {
                pos++;
            }
            if (pos >= len)
            {
                break; // trailing comma + whitespace (view-sort-grammar: "S ASC, ")
            }

            string colName;
            if (sortText[pos] == '[')
            {
                pos++;
                int close = sortText.IndexOf(']', pos);
                if (close < 0 || close == pos)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return null;
                }
                colName = sortText[pos..close];
                pos = close + 1;
            }
            else
            {
                int start = pos;
                while (pos < len && !char.IsWhiteSpace(sortText[pos]) && sortText[pos] != ',')
                {
                    pos++;
                }
                if (pos == start)
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return null;
                }
                colName = sortText[start..pos];
            }

            while (pos < len && char.IsWhiteSpace(sortText[pos]))
            {
                pos++;
            }

            bool desc = false;
            if (pos < len && sortText[pos] != ',')
            {
                int dirStart = pos;
                while (pos < len && !char.IsWhiteSpace(sortText[pos]) && sortText[pos] != ',')
                {
                    pos++;
                }
                string dir = sortText[dirStart..pos];
                if (string.Equals(dir, "ASC", StringComparison.OrdinalIgnoreCase))
                {
                    desc = false;
                }
                else if (string.Equals(dir, "DESC", StringComparison.OrdinalIgnoreCase))
                {
                    desc = true;
                }
                else
                {
                    adoError = AdoErrors.ArgumentsWrongType;
                    return null;
                }
                while (pos < len && char.IsWhiteSpace(sortText[pos]))
                {
                    pos++;
                }
            }

            rawKeys.Add((colName, desc));

            if (pos < len)
            {
                if (sortText[pos] != ',')
                {
                    adoError = AdoErrors.ArgumentsWrongType; // e.g. "L DESC DESC"
                    return null;
                }
                pos++;
            }
        }

        if (rawKeys.Count == 0)
        {
            return new AdoSortSpec([]);
        }

        var resolved = new AdoSortKey[rawKeys.Count];
        for (int i = 0; i < rawKeys.Count; i++)
        {
            int ordinal = AdoFilterExpression.FindColumnOrdinal(columns, rawKeys[i].Name);
            if (ordinal < 0)
            {
                adoError = AdoErrors.ItemNotFound;
                return null;
            }
            int adoType = columns[ordinal].AdoType;
            // evidence/r-ado-1b/view-sort-grammar (#77, #81): Memo (203) and LongBinary (205) fail 0x80040E60 from Microsoft Cursor Engine.
            if (adoType is 203 or 205)
            {
                cursorEngineFailure = AdoObject.CursorEngineFailure(AdoObject.HResultCannotSort, "Sort order cannot be applied.");
                return null;
            }
            if (adoType == 204)
            {
                cursorEngineFailure = Failure.NotImplemented("Recordset.Sort on adVarBinary (204) column (not recorded; only Memo 203 and LongBinary 205 are recorded)");
                return null;
            }
            resolved[i] = new AdoSortKey(ordinal, rawKeys[i].Descending);
        }
        return new AdoSortSpec(resolved);
    }

    public bool SortIndices(List<int> indices, IReadOnlyList<MemoryCell[]> rows, out Failure? failure)
    {
        failure = null;
        // Validate every non-null cell VarType up front so an unmodelled VarType or per-cell refusal sentinel is refused deterministically even on 1-row cursors.
        foreach (int rowIdx in indices)
        {
            MemoryCell[] row = rows[rowIdx];
            foreach (AdoSortKey key in Keys)
            {
                MemoryCell cell = row[key.ColumnOrdinal];
                if (cell.Refusal is { } refusal)
                {
                    failure = refusal;
                    return false;
                }
                VarType vt = (VarType)cell.Value!.Pointer->Vt;
                if (!IsSupportedSortVarType(vt))
                {
                    failure = Failure.NotImplemented($"Recordset.Sort comparison of VarType {vt} (not recorded)");
                    return false;
                }
            }
        }
        Failure? sortFailure = null;
        indices.Sort((a, b) => CompareRows(a, rows[a], b, rows[b], ref sortFailure));
        if (sortFailure is not null)
        {
            failure = sortFailure;
            return false;
        }
        return true;
    }

    public bool SortClientRows(List<ClientRow> rows, out Failure? failure)
    {
        failure = null;
        if (rows.Count == 0 || Keys.Length == 0)
        {
            return true;
        }
        foreach (ClientRow row in rows)
        {
            foreach (AdoSortKey key in Keys)
            {
                VarType vt = (VarType)row.Current[key.ColumnOrdinal].Pointer->Vt;
                if (!IsSupportedSortVarType(vt))
                {
                    failure = Failure.NotImplemented($"Recordset.Sort comparison of VarType {vt} (not recorded)");
                    return false;
                }
            }
        }
        var order = new Dictionary<ClientRow, int>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            order[rows[i]] = i;
        }
        Failure? sortFailure = null;
        rows.Sort((a, b) => CompareClientRows(order[a], a, order[b], b, ref sortFailure));
        if (sortFailure is not null)
        {
            failure = sortFailure;
            return false;
        }
        return true;
    }

    private int CompareClientRows(int indexA, ClientRow rowA, int indexB, ClientRow rowB, ref Failure? failure)
    {
        foreach (AdoSortKey key in Keys)
        {
            int cmp = CompareCells(rowA.Current[key.ColumnOrdinal], rowB.Current[key.ColumnOrdinal], ref failure);
            if (failure is not null)
            {
                return 0;
            }
            if (cmp != 0)
            {
                return key.Descending ? -cmp : cmp;
            }
        }
        return Keys[^1].Descending ? indexB.CompareTo(indexA) : indexA.CompareTo(indexB);
    }

    private static bool IsSupportedSortVarType(VarType vt) => vt is
        VarType.Null or VarType.Empty or VarType.Bstr or VarType.Bool or VarType.Date or VarType.R8 or VarType.R4 or
        VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.Currency or VarType.Decimal;

    public int CompareRows(int indexA, MemoryCell[] rowA, int indexB, MemoryCell[] rowB, ref Failure? failure)
    {
        foreach (AdoSortKey key in Keys)
        {
            int cmp = CompareCells(rowA[key.ColumnOrdinal].Value!, rowB[key.ColumnOrdinal].Value!, ref failure);
            if (failure is not null)
            {
                return 0;
            }
            if (cmp != 0)
            {
                return key.Descending ? -cmp : cmp;
            }
        }
        // evidence/r-ado-1b/view-sort-grammar: ties preserve original order on ASC and reverse on DESC of the last sort key.
        return Keys[^1].Descending ? indexB.CompareTo(indexA) : indexA.CompareTo(indexB);
    }

    private static int CompareCells(OwnedVariant a, OwnedVariant b, ref Failure? failure)
    {
        NativeVariant* va = a.Pointer;
        NativeVariant* vb = b.Pointer;
        VarType ta = (VarType)va->Vt;
        VarType tb = (VarType)vb->Vt;
        bool na = ta is VarType.Null or VarType.Empty;
        bool nb = tb is VarType.Null or VarType.Empty;
        if (na && nb)
        {
            return 0;
        }
        if (na)
        {
            return -1; // NULL sorts first in ASC, last in DESC (view-sort-grammar)
        }
        if (nb)
        {
            return 1;
        }

        switch (ta)
        {
            case VarType.Bstr when tb == VarType.Bstr:
                {
                    ReadOnlySpan<char> sa = va->Pointer == 0 ? ReadOnlySpan<char>.Empty : Bstrs.Chars(va->Pointer);
                    ReadOnlySpan<char> sb = vb->Pointer == 0 ? ReadOnlySpan<char>.Empty : Bstrs.Chars(vb->Pointer);
                    return sa.CompareTo(sb, StringComparison.OrdinalIgnoreCase);
                }
            // evidence/r-ado-1b/view-sort-grammar: False sorts before True in ASC.
            case VarType.Bool when tb == VarType.Bool:
                return ((short)va->Int64 != 0).CompareTo((short)vb->Int64 != 0);
            case VarType.Date when tb == VarType.Date:
            case VarType.R8 when tb == VarType.R8:
                return va->Double.CompareTo(vb->Double);
            case VarType.R4 when tb == VarType.R4:
                return BitConverter.Int32BitsToSingle((int)va->Int64).CompareTo(BitConverter.Int32BitsToSingle((int)vb->Int64));
            case VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.Currency or VarType.Decimal
                when tb is VarType.UI1 or VarType.I2 or VarType.I4 or VarType.I8 or VarType.Currency or VarType.Decimal:
                return AdoFilterExpression.CellAsDecimal(a).CompareTo(AdoFilterExpression.CellAsDecimal(b));
            default:
                failure ??= Failure.NotImplemented($"Recordset.Sort comparison of VarTypes ({ta}, {tb}) (not recorded)");
                return 0;
        }
    }
}
