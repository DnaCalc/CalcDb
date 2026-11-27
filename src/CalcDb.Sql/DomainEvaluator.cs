using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Values;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>
/// Evaluates the 8 SQL domain aggregate functions (<c>DLookup</c>, <c>DCount</c>, <c>DSum</c>, <c>DMin</c>, <c>DMax</c>,
/// <c>DAvg</c>, <c>DFirst</c>, <c>DLast</c>) against an <see cref="ISqlDatabase"/> (II-10 §27; qualified.md
/// R-EXPR-1 DomainFunction; <c>evidence/r-q52-ddl-make-domain/domain-*</c>, <c>r-q-expr-sql/domain-*</c>,
/// <c>r-def-expr-sql/expr-domain-*</c>, <c>r-fix-expr-sql/domain-per-row-no-nz</c>, <c>r-q2-expr-sql/domain-*</c>).
/// </summary>
internal static unsafe class DomainEvaluator
{
    public static bool Evaluate(
        ISqlDatabase database,
        DomainFunction function,
        ReadOnlySpan<NativeVariant> args,
        int site,
        out NativeVariant result,
        out ExpressionFailure? failure)
    {
        result = default;
        failure = null;

        if (args.Length is < 2 or > 3)
        {
            failure = Ace(3075, ExpressionMessages.WrongArgumentCount, site);
            return false;
        }

        // r-q52-ddl-make-domain/domain-null-empty-errors-dao-*, domain-criteria-perrow-dao-*,
        // r-def-expr-sql/expr-domain-criteria-and-failures: any Null argument (expr, domain, or criteria) fails 3464.
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Vt is (ushort)VarType.Null or (ushort)VarType.Empty)
            {
                failure = Ace(AceError.TypeMismatch, SqlMessages.DataTypeMismatch, site);
                return false;
            }
        }

        if (!TryToString(args[0], database.Context, site, out string exprText, out failure)
            || !TryToString(args[1], database.Context, site, out string rawDomain, out failure))
        {
            return false;
        }

        string criteriaText = "";
        if (args.Length == 3 && !TryToString(args[2], database.Context, site, out criteriaText, out failure))
        {
            return false;
        }

        string domainName = NormalizeDomainName(rawDomain);
        TableDefinition? table = database.Table(domainName);
        if (table is null)
        {
            if (database.IsQuery(domainName))
            {
                failure = ExpressionFailure.Open(
                    ExpressionPhase.Evaluate,
                    "NotModelled.QuerySource",
                    $"{function} over saved query '{domainName}' (saved-query domain source is not recorded)");
                return false;
            }
            failure = Ace(3078, SqlMessages.NoInputTable(domainName), site);
            return false;
        }
        if (database.IsSystemTable(domainName, table))
        {
            // r-def-expr-sql/expr-domain-criteria-and-failures #287: DLookup('Name', 'MSysObjects') fails 3112.
            failure = Ace(3112, SqlMessages.NoReadPermission(domainName), site);
            return false;
        }

        // Parse criteria (if present and non-empty). Empty '' is treated as no criteria; whitespace-only ' ' fails 3075.
        ExpressionSyntax? criteriaSyntax = null;
        if (criteriaText.Length > 0)
        {
            if (criteriaText.AsSpan().Trim().IsEmpty)
            {
                failure = Ace(3075, $"Syntax error (missing operator){SqlFailure.QueryExpression}{criteriaText}'.", site);
                return false;
            }
            criteriaSyntax = ExpressionSyntax.Parse(criteriaText, SqlParser.TryParseNestedSelect);
            if (criteriaSyntax.Failure is { } critErr)
            {
                criteriaSyntax.Dispose();
                failure = FormatSyntaxError(critErr, criteriaText, site);
                return false;
            }
        }

        bool isCountStar = false;
        ExpressionSyntax? exprSyntax = null;
        try
        {
            ReadOnlySpan<char> trimmedExpr = exprText.AsSpan().Trim();
            if (trimmedExpr.IsEmpty)
            {
                failure = Ace(3075, $"Syntax error (missing operator){SqlFailure.QueryExpression}{exprText}'.", site);
                return false;
            }
            if (trimmedExpr.SequenceEqual("*"))
            {
                if (function == DomainFunction.DCount)
                {
                    isCountStar = true;
                }
                else
                {
                    // r-def-expr-sql/expr-domain-criteria-and-failures #224: DLookup('*', 'Dm') fails 3075.
                    failure = Ace(3075, $"Syntax error (missing operator){SqlFailure.QueryExpression}{exprText}'.", site);
                    return false;
                }
            }
            else
            {
                exprSyntax = ExpressionSyntax.Parse(exprText, SqlParser.TryParseNestedSelect);
                if (exprSyntax.Failure is { } exprErr)
                {
                    // r-def-expr-sql/expr-domain-aggregate-values #84: DCount('DISTINCT G', 'Dm') quotes 'Count(DISTINCT G)';
                    // r-def-expr-sql/expr-domain-criteria-and-failures #231: DLookup('ID, N', 'Dm') quotes 'ID, N'.
                    string quote = function == DomainFunction.DCount ? $"Count({exprText})" : exprText;
                    failure = FormatSyntaxError(exprErr, quote, site);
                    return false;
                }
            }

            // r-def-expr-sql/expr-domain-criteria-and-failures #98: DLookup('Count(*)', 'Dm') evaluates the SQL aggregate.
            if (!isCountStar && exprSyntax!.Root is { } er && HasSqlAggregate(er))
            {
                return EvaluateAggregateExpr(database, function, exprText, domainName, criteriaText, site, out result, out failure);
            }

            if (!BoundSelect.BindDomainExpressions(
                table,
                domainName,
                exprText,
                exprSyntax?.Root,
                criteriaText,
                criteriaSyntax?.Root,
                database,
                out BoundSelect.DomainPlan? plan,
                out SqlFailure? bindFail))
            {
                failure = FromSqlFailure(bindFail!, site);
                return false;
            }

            using (plan)
            {
                // r-q52-ddl-make-domain/domain-null-empty-errors-dao-*, r-def-expr-sql/expr-domain-aggregate-values:
                // DSum/DAvg on a Text column fails 3464 regardless of row count.
                if (plan!.ExprProgram?.BareField is { Column: { } bareCol })
                {
                    if (function is DomainFunction.DSum or DomainFunction.DAvg && bareCol.TypeCode == NativeTypes.Text)
                    {
                        failure = Ace(AceError.TypeMismatch, SqlMessages.DataTypeMismatch, site);
                        return false;
                    }
                    if (bareCol.TypeCode is NativeTypes.Memo or NativeTypes.Ole
                        && function is DomainFunction.DSum or DomainFunction.DAvg or DomainFunction.DMin or DomainFunction.DMax)
                    {
                        // No sealed ACE record covers DSum/DAvg/DMin/DMax on Memo or OLE columns; refuse honestly.
                        failure = ExpressionFailure.Open(
                            ExpressionPhase.Evaluate,
                            "NotModelled.DomainMemoAggregate",
                            $"{function} over Memo/OLE column '{bareCol.Name}' (ACE's domain-function error is not recorded)");
                        return false;
                    }
                }

                RowReader? reader = database.OpenRows(table, out string? storageFail);
                if (reader is null)
                {
                    failure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Storage", storageFail ?? "the table cannot be read");
                    return false;
                }
                reader.QueryTextRoundTrip = true;
                using (reader)
                {
                    return ScanAndAggregate(database, function, isCountStar, plan, reader, site, out result, out failure);
                }
            }
        }
        finally
        {
            exprSyntax?.Dispose();
            criteriaSyntax?.Dispose();
        }
    }

    private static bool ScanAndAggregate(
        ISqlDatabase database,
        DomainFunction function,
        bool isCountStar,
        BoundSelect.DomainPlan plan,
        RowReader reader,
        int site,
        out NativeVariant result,
        out ExpressionFailure? failure)
    {
        result = default;
        failure = null;
        var row = new DomainRow(reader, plan.Subqueries, database);
        long count = 0;
        double sum = 0;
        bool seen = false;
        NativeVariant best = default;
        TextCollation? collation = database.Context.DatabaseOrder;

        try
        {
            for (RowId? rid = reader.Scan.Next(null); rid is { } r; rid = reader.Scan.Next(r))
            {
                reader.Position(r);
                if (plan.CriteriaProgram is { } crit)
                {
                    if (!crit.Evaluate(row))
                    {
                        failure = crit.LastFailure! with { FromDomainFunction = true };
                        return false;
                    }
                    if (!TryIsTruthy(crit.Value, database.Context, site, out bool match, out failure))
                    {
                        return false;
                    }
                    if (!match)
                    {
                        continue;
                    }
                }

                if (isCountStar)
                {
                    count++;
                    continue;
                }

                BoundExpression expr = plan.ExprProgram!;
                if (!expr.Evaluate(row))
                {
                    failure = expr.LastFailure! with { FromDomainFunction = true };
                    return false;
                }
                NativeVariant val = expr.Value;
                bool isNull = val.Vt == (ushort)VarType.Null;

                switch (function)
                {
                    case DomainFunction.DCount:
                        if (!isNull)
                        {
                            count++;
                        }
                        break;

                    case DomainFunction.DLookup:
                    case DomainFunction.DFirst:
                        // First matching row in scan/insertion order (even if expr evaluates to Null).
                        result = SelectValues.Copy(val);
                        return true;

                    case DomainFunction.DLast:
                        SelectValues.Clear(ref best);
                        best = SelectValues.Copy(val);
                        seen = true;
                        break;

                    case DomainFunction.DSum:
                    case DomainFunction.DAvg:
                        if (isNull)
                        {
                            break;
                        }
                        if (!TryToDoubleForSum(val, out double d))
                        {
                            failure = Ace(AceError.TypeMismatch, SqlMessages.DataTypeMismatch, site);
                            return false;
                        }
                        sum += d;
                        count++;
                        if (double.IsInfinity(sum))
                        {
                            failure = Ace(AceError.Overflow, SqlMessages.Overflow, site);
                            return false;
                        }
                        break;

                    case DomainFunction.DMin:
                    case DomainFunction.DMax:
                        if (isNull)
                        {
                            break;
                        }
                        if (!seen)
                        {
                            best = SelectValues.Copy(val);
                            seen = true;
                            break;
                        }
                        if (!RelationalOrder.TryCompare(val, best, collation, out int cmp))
                        {
                            failure = ExpressionFailure.Open(
                                ExpressionPhase.Evaluate,
                                "NotModelled.MixedOrderTypes",
                                $"{function} over values the relational order does not rank");
                            return false;
                        }
                        if (function == DomainFunction.DMin ? cmp < 0 : cmp > 0)
                        {
                            SelectValues.Clear(ref best);
                            best = SelectValues.Copy(val);
                        }
                        break;
                }
            }

            if (reader.Scan.LastFailure is { } storage)
            {
                failure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.Storage", storage.Predicate);
                return false;
            }

            switch (function)
            {
                case DomainFunction.DCount:
                    result = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = (uint)(int)count };
                    return true;
                case DomainFunction.DSum:
                    result = count == 0 ? ScalarLiterals.Null : new NativeVariant { Vt = (ushort)VarType.R8, Double = sum };
                    return true;
                case DomainFunction.DAvg:
                    result = count == 0 ? ScalarLiterals.Null : new NativeVariant { Vt = (ushort)VarType.R8, Double = sum / count };
                    return true;
                case DomainFunction.DLookup:
                case DomainFunction.DFirst:
                    result = ScalarLiterals.Null;
                    return true;
                case DomainFunction.DLast:
                case DomainFunction.DMin:
                case DomainFunction.DMax:
                    result = seen ? best : ScalarLiterals.Null;
                    best = default;
                    return true;
                default:
                    failure = ExpressionFailure.Open(ExpressionPhase.Evaluate, "NotModelled.DomainFunction", $"{function} is not modelled");
                    return false;
            }
        }
        finally
        {
            SelectValues.Clear(ref best);
        }
    }

    private static bool EvaluateAggregateExpr(
        ISqlDatabase database,
        DomainFunction function,
        string exprText,
        string domainName,
        string criteriaText,
        int site,
        out NativeVariant result,
        out ExpressionFailure? failure)
    {
        result = default;
        failure = null;
        if (function != DomainFunction.DLookup)
        {
            failure = ExpressionFailure.Open(
                ExpressionPhase.Evaluate,
                "NotModelled.DomainFunction",
                $"{function} with an aggregate expression '{exprText}' is not recorded");
            return false;
        }
        string sql = criteriaText.Length > 0
            ? $"SELECT {exprText} FROM [{domainName}] WHERE {criteriaText}"
            : $"SELECT {exprText} FROM [{domainName}]";
        using BoundSelect? plan = BoundSelect.Prepare(sql, database, out SqlFailure? prepFail);
        if (plan is null)
        {
            failure = FromSqlFailure(prepFail!, site);
            return false;
        }
        if (plan.Parameters.Count > 0)
        {
            failure = Ace(3061, SqlMessages.TooFewParameters(plan.Parameters.Count), site);
            return false;
        }
        using SelectCursor? cursor = SelectCursor.Open(plan, database, ResultKind.Snapshot, out SqlFailure? openFail);
        if (cursor is null)
        {
            failure = FromSqlFailure(openFail!, site);
            return false;
        }
        if (cursor.Eof)
        {
            result = ScalarLiterals.Null;
            return true;
        }
        if (!cursor.Read(0, out NativeVariant v, out SqlFailure? readFail))
        {
            failure = FromSqlFailure(readFail!, site);
            return false;
        }
        result = SelectValues.Copy(v);
        return true;
    }

    private static bool HasSqlAggregate(SyntaxNode root)
    {
        foreach (SyntaxNode n in BoundSelect.Walk(root))
        {
            if (BoundSelect.AggregateOf(n) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Strips matching outer brackets (<c>[Dm]</c> -&gt; <c>Dm</c>) or an unmatched leading bracket
    /// (<c>[SELECT ID FROM Dm]. AS x</c> -&gt; <c>SELECT ID FROM Dm]. AS x</c>; r-def-expr-sql/expr-domain-criteria-and-failures #126).
    /// </summary>
    private static string NormalizeDomainName(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']' && raw.IndexOf(']', 1) == raw.Length - 1)
        {
            return raw[1..^1];
        }
        if (raw.Length >= 1 && raw[0] == '[')
        {
            return raw[1..];
        }
        return raw;
    }

    private static bool TryToString(in NativeVariant value, ExpressionContext context, int site, out string text, out ExpressionFailure? failure)
    {
        text = "";
        failure = null;
        if (value.Vt == (ushort)VarType.Bstr)
        {
            text = value.Pointer == 0 ? "" : Bstrs.Chars(value.Pointer).ToString();
            return true;
        }
        if (value.Vt == (ushort)VarType.Bool)
        {
            // r-def-expr-sql/expr-domain-criteria-and-failures #154: DLookup('ID', 'Dm', True) stringifies True as "-1".
            text = (short)value.Int64 != 0 ? "-1" : "0";
            return true;
        }
        ScalarOutcome conv = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CStr, site, OperandContract.Expression), value, context);
        if (!conv.Succeeded)
        {
            ScalarFailure f = conv.Failure;
            failure = f.Kind == ScalarFailureKind.NotModelled
                ? ExpressionFailure.Open(ExpressionPhase.Evaluate, $"NotModelled.{f.Case}", f.Describe()) with { Site = site, FromDomainFunction = true }
                : Ace(f.Number, AceError.Describe(f.Number), site);
            return false;
        }
        try
        {
            text = conv.Value.Pointer == 0 ? "" : Bstrs.Chars(conv.Value.Pointer).ToString();
            return true;
        }
        finally
        {
            conv.Clear();
        }
    }

    /// <summary>
    /// Criteria truthiness inside a domain function (r-def-expr-sql/expr-domain-criteria-and-failures #147, #154):
    /// Null is false, any non-zero numeric value (e.g. <c>4</c> or <c>True</c>) is true, and text coerces via <c>CBool</c>.
    /// </summary>
    private static bool TryIsTruthy(in NativeVariant value, ExpressionContext context, int site, out bool match, out ExpressionFailure? failure)
    {
        match = false;
        failure = null;
        switch ((VarType)value.Vt)
        {
            case VarType.Null or VarType.Empty:
                return true;
            case VarType.Bool or VarType.I2:
                match = (short)value.Int64 != 0;
                return true;
            case VarType.UI1:
                match = (byte)value.Int64 != 0;
                return true;
            case VarType.I4:
                match = (int)value.Int64 != 0;
                return true;
            case VarType.I8 or VarType.Currency:
                match = value.Int64 != 0;
                return true;
            case VarType.R4:
                match = BitConverter.Int32BitsToSingle((int)value.Int64) != 0f;
                return true;
            case VarType.R8 or VarType.Date:
                match = value.Double != 0.0;
                return true;
            case VarType.Decimal:
                match = OwnedVariant.ReadDecimal(value).ToDecimal() != 0m;
                return true;
            case VarType.Bstr:
            {
                ScalarOutcome cb = ScalarKernel.Apply(ScalarDescriptor.Unary(ScalarOperation.CBool, site, OperandContract.Expression), value, context);
                if (!cb.Succeeded)
                {
                    failure = Ace(AceError.TypeMismatch, SqlMessages.DataTypeMismatch, site);
                    return false;
                }
                match = (short)cb.Value.Int64 != 0;
                return true;
            }
            default:
                failure = Ace(AceError.TypeMismatch, SqlMessages.DataTypeMismatch, site);
                return false;
        }
    }

    private static bool TryToDoubleForSum(in NativeVariant value, out double result)
    {
        if (RelationalOrder.Number(value, out result))
        {
            return true;
        }
        if (value.Vt == (ushort)VarType.Decimal)
        {
            result = (double)OwnedVariant.ReadDecimal(value).ToDecimal();
            return true;
        }
        result = 0;
        return false;
    }

    private static ExpressionFailure FormatSyntaxError(ExpressionFailure parseError, string quote, int site)
    {
        if (parseError.IsNotModelled)
        {
            return parseError with { Site = site, FromDomainFunction = true };
        }
        const string InExpression = " in expression.";
        if (parseError.Number is 3077 or 3075 && parseError.Message.EndsWith(InExpression, StringComparison.Ordinal))
        {
            return Ace(3075, $"{parseError.Message[..^InExpression.Length]}{SqlFailure.QueryExpression}{quote}'.", site);
        }
        return Ace(parseError.Number, parseError.Message, site);
    }

    private static ExpressionFailure FromSqlFailure(SqlFailure sf, int site) =>
        sf.IsNotModelled
            ? ExpressionFailure.Open(ExpressionPhase.Evaluate, sf.NotModelled!, sf.Message) with { Site = site, FromDomainFunction = true }
            : Ace(sf.Number, sf.Message, site);

    private static ExpressionFailure Ace(int number, string message, int site) =>
        ExpressionFailure.Ace(ExpressionPhase.Evaluate, number, message) with { Site = site, FromDomainFunction = true };

    private sealed class DomainRow(RowReader reader, List<BoundSubquery> subqueries, ISqlDatabase database) : IExpressionRow
    {
        public IRecordCursor? Source(int source) => source == 0 ? reader : null;

        public NativeVariant Slot(int slot) => ScalarLiterals.Null;

        public bool EvaluateSubquery(int subqueryId, in NativeVariant leftOperand, out NativeVariant result, out ExpressionFailure? failure) =>
            SelectCursor.EvaluateSubquery(subqueries[subqueryId], this, [], database, leftOperand, out result, out failure);

        public bool EvaluateDomain(DomainFunction function, ReadOnlySpan<NativeVariant> args, int site, out NativeVariant result, out ExpressionFailure? failure) =>
            DomainEvaluator.Evaluate(database, function, args, site, out result, out failure);
    }
}
