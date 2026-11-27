using System.Text;

namespace CalcDb.Catalog;

/// <summary>The public SQL view of a saved query: the text, or the named reason it is not derived.</summary>
public readonly record struct RenderedSql(string? Text, string? NotModelled)
{
    public static RenderedSql Of(string text) => new(text, null);

    public static RenderedSql Refuse(string why) => new(null, why);
}

/// <summary>
/// QueryDef.SQL of a saved query, rendered from its role rows as ACE renders them at open (R-CAT-1 CAT-Q, qualified.md;
/// II-06 §10: a serializer of the public string, distinct from the stored expressions). Only the shapes the retained
/// records decide are rendered; every other shape is a named refusal, never a guessed text. The rules, each from the
/// reopened text of r-cat-1/querydefs and the CAT-Q matrix:
/// <list type="bullet">
/// <item>clauses on lines of their own, CR LF between them, and a closing <c>;</c> plus CR LF;</item>
/// <item>stored expressions, aliases and names verbatim (no quoting added: an alias stored as <c>Bä€</c> stays bare);</item>
/// <item><c>PARAMETERS a Long, b Text ( 20 );</c> on the first line; projections joined by <c>", "</c>, an alias as
/// <c>expr AS alias</c>; <c>DISTINCT</c>, <c>DISTINCTROW</c>, <c>TOP n</c> and <c>TOP n PERCENT</c> from the option bits;</item>
/// <item>a descending sort term as <c>expr DESC</c> followed by <c>" , "</c> before the next term (CAT-Q's
/// <c>ORDER BY t.id DESC , t.a</c>);</item>
/// <item>DDL and pass-through: the type row's Expression alone, with nothing appended; UNION: the first fragment without
/// its trailing blanks, <c>UNION</c> or <c>UNION ALL</c> by option bit 0002, the second fragment, then <c>;</c> CR LF.</item>
/// </list>
/// </summary>
public static class QuerySql
{
    private const string Line = "\r\n";

    // Attribute-3 option bits (II-06 §8.1).
    private const short Star = 0x0001;
    private const short Distinct = 0x0002;
    private const short OwnerAccess = 0x0004;
    private const short DistinctRow = 0x0008;
    private const short Top = 0x0010;
    private const short Percent = 0x0020;

    // Crosstab output flags (II-06 §10.1): pivot 0001, ordinary output 0002, transform neither.
    private const short Pivot = 0x0001;
    private const short CrosstabOutput = 0x0002;

    public static RenderedSql Render(SavedQuery q)
    {
        if (q.OrderingFailure is { } ordering)
        {
            return RenderedSql.Refuse("the row ordering profile: " + ordering);
        }
        if (q.Family is not { } family)
        {
            return RenderedSql.Refuse($"query flag class 0x{q.FlagClass:X2}");
        }
        if (q.Rows.Any(r => r.Attribute is QueryAttributes.ExternalDatabase))
        {
            return RenderedSql.Refuse("an external-database row (attribute 4)");
        }
        return family switch
        {
            QueryFamily.DataDefinition or QueryFamily.PassThrough => q.TypeRow?.Expression is { } verbatim
                ? RenderedSql.Of(verbatim)
                : RenderedSql.Refuse($"a {family} query without type-row text"),
            QueryFamily.Union => Union(q),
            _ => Statement(q, family),
        };
    }

    private static RenderedSql Union(SavedQuery q)
    {
        QueryRow[] fragments = [.. q.Role(QueryAttributes.Source)];
        if (fragments is not [{ Name2: "X7YZ_____1", Expression: { } first }, { Name2: "X7YZ_____2", Expression: { } second }]
            || q.Rows.Any(r => r.Attribute is not (QueryAttributes.Start or QueryAttributes.End or QueryAttributes.Type or QueryAttributes.Source or QueryAttributes.Options)))
        {
            return RenderedSql.Refuse("a UNION whose rows are not exactly the two fragment slots and its options");
        }
        short options = Options(q, out string? optionFailure);
        if (optionFailure is not null || (options & ~(Star | Distinct)) != 0)
        {
            return RenderedSql.Refuse(optionFailure ?? $"UNION option bits 0x{options:X4}");
        }
        return RenderedSql.Of(first.TrimEnd(' ') + ((options & Distinct) != 0 ? " UNION " : " UNION ALL ") + second + ";" + Line);
    }

    private static RenderedSql Statement(SavedQuery q, QueryFamily family)
    {
        var sql = new StringBuilder();
        if (Parameters(q, sql) is { } parameterFailure)
        {
            return RenderedSql.Refuse(parameterFailure);
        }
        short options = Options(q, out string? optionFailure);
        if (optionFailure is not null)
        {
            return RenderedSql.Refuse(optionFailure);
        }
        bool ownerAccess = (options & OwnerAccess) != 0;
        if (ownerAccess && (family != QueryFamily.Select || (options & ~OwnerAccess) != 0))
        {
            return RenderedSql.Refuse("WITH OWNERACCESS OPTION combined with non-SELECT or other SELECT option bits (only plain SELECT WITH OWNERACCESS OPTION is observed in r-q50-jk44-ddl-vis/ddl-view-create-drop-*)");
        }
        short selectOptions = (short)(options & ~OwnerAccess);
        QueryRow[] outputs = [.. q.Role(QueryAttributes.Output)];
        QueryRow? type = q.TypeRow;
        if (family != QueryFamily.Select && type is null)
        {
            return RenderedSql.Refuse($"a {family} query without its type row");
        }
        if (type is not null && (type.Name2 is not null || (family != QueryFamily.DataDefinition && type.Expression is not null)))
        {
            return RenderedSql.Refuse("a type row with an external target");
        }
        switch (family)
        {
            case QueryFamily.Select:
            case QueryFamily.MakeTable:
            case QueryFamily.Crosstab:
            {
                QueryRow[] projected = outputs;
                if (family == QueryFamily.Crosstab)
                {
                    QueryRow[] transform = [.. outputs.Where(o => (o.Flag ?? 0) == 0)];
                    if (transform.Length != 1)
                    {
                        return RenderedSql.Refuse($"a crosstab with {transform.Length} transform rows");
                    }
                    sql.Append("TRANSFORM ").Append(Projection(transform[0])).Append(Line);
                    projected = [.. outputs.Where(o => o.Flag == CrosstabOutput)];
                }
                else if (outputs.Any(o => (o.Flag ?? 0) != 0 || o.Name2 is not null))
                {
                    return RenderedSql.Refuse("a SELECT output row with flags or a target");
                }
                if (Select(sql, selectOptions, TopCount(q), projected) is { } selectFailure)
                {
                    return RenderedSql.Refuse(selectFailure);
                }
                if (family == QueryFamily.MakeTable)
                {
                    if (type!.Name1 is not { } target)
                    {
                        return RenderedSql.Refuse("a make-table query without a target");
                    }
                    sql.Append(" INTO ").Append(target);
                }
                break;
            }
            case QueryFamily.Append:
            {
                if (type!.Name1 is not { } target || outputs.Length == 0)
                {
                    return RenderedSql.Refuse("an append query without a target or outputs");
                }
                if (outputs.All(o => o.Name2 is not null && o.Expression is not null && o.Name1 is null && o.Flag == unchecked((short)0x8000)))
                {
                    if (options != 0 || q.Role(QueryAttributes.Source).Any() || q.Role(QueryAttributes.Join).Any()
                        || q.Role(QueryAttributes.Where).Any() || q.Role(QueryAttributes.GroupBy).Any()
                        || q.Role(QueryAttributes.Having).Any() || q.Role(QueryAttributes.OrderBy).Any())
                    {
                        return RenderedSql.Refuse("an INSERT VALUES query with options or table clauses (not observed)");
                    }
                    // r-q39-querydef-bytes/action-insert-values-accdb: INSERT INTO T ( ID, V, W )\r\nVALUES (1, 10, 'a');\r\n
                    sql.Append("INSERT INTO ").Append(target).Append(" ( ").AppendJoin(", ", outputs.Select(o => o.Name2)).Append(" )").Append(Line)
                       .Append("VALUES (").AppendJoin(", ", outputs.Select(o => o.Expression)).Append(");").Append(Line);
                    return RenderedSql.Of(sql.ToString());
                }
                if (outputs.Any(o => o.Name2 is null || (o.Flag ?? 0) != 0))
                {
                    return RenderedSql.Refuse("an append query whose outputs are not all mapped to target columns");
                }
                sql.Append("INSERT INTO ").Append(target).Append(" ( ").AppendJoin(", ", outputs.Select(o => o.Name2)).Append(" )").Append(Line);
                if (Select(sql, options, TopCount(q), outputs) is { } selectFailure)
                {
                    return RenderedSql.Refuse(selectFailure);
                }
                break;
            }
            case QueryFamily.Update:
            {
                QueryRow[] sources = [.. q.Role(QueryAttributes.Source)];
                if (outputs.Length == 0 || outputs.Any(o => o.Name2 is null || o.Expression is null || o.Name1 is not null || (o.Flag ?? 0) != 0)
                    || sources is not [{ Name1: { } table, Name2: null, Expression: null }] || options != 0
                    || q.Role(QueryAttributes.Join).Any())
                {
                    return RenderedSql.Refuse("an UPDATE other than assignments to one table");
                }
                // r-q39-querydef-bytes/action-update-accdb and action-update-mdb: UPDATE T SET V = 1, W = 'x'\r\nWHERE ID = 1;\r\n
                sql.Append("UPDATE ").Append(table).Append(" SET ")
                   .AppendJoin(", ", outputs.Select(o => $"{o.Name2} = {o.Expression}"));
                return Tail(q, sql, from: false, family);
            }
            case QueryFamily.Delete:
            {
                if (outputs.Length != 0 || options != 0)
                {
                    return RenderedSql.Refuse("a DELETE with output rows or options");
                }
                sql.Append("DELETE *");
                break;
            }
            default:
                return RenderedSql.Refuse($"the {family} family");
        }
        return Tail(q, sql, from: true, family, ownerAccess);
    }

    private static string? Parameters(SavedQuery q, StringBuilder sql)
    {
        IReadOnlyList<QueryParameter> declared = q.DeclaredParameters;
        if (declared.Count == 0)
        {
            return null;
        }
        sql.Append("PARAMETERS ");
        for (int i = 0; i < declared.Count; i++)
        {
            QueryParameter p = declared[i];
            string? typeName = ParameterTypeName(p.NativeType);
            if (typeName is null)
            {
                return $"PARAMETERS type {p.NativeType} (its spelling is not observed)";
            }
            sql.Append(i == 0 ? "" : ", ").Append(p.Name).Append(' ').Append(typeName);
            if (p.NativeType == CalcDb.Format.NativeTypes.Text && p.TextSize is > 0 and int size)
            {
                sql.Append(" ( ").Append(size).Append(" )");
            }
        }
        sql.Append(';').Append(Line);
        return null;
    }

    /// <summary>The declaration type spellings the reopened SQL shows (r-cat-1/querydefs: Short, Long, Text).</summary>
    private static string? ParameterTypeName(short nativeType) => nativeType switch
    {
        CalcDb.Format.NativeTypes.Integer => "Short",
        CalcDb.Format.NativeTypes.Long => "Long",
        CalcDb.Format.NativeTypes.Text => "Text",
        _ => null,
    };

    private static short Options(SavedQuery q, out string? failure)
    {
        failure = null;
        QueryRow[] rows = [.. q.Role(QueryAttributes.Options)];
        if (rows.Length > 1)
        {
            failure = "more than one options row";
            return 0;
        }
        return rows.Length == 0 ? (short)0 : rows[0].Flag ?? 0;
    }

    /// <summary>The TOP count, stored as text in the options row's Name1 (r-cat-1 query fixture: "2", "50").</summary>
    private static string? TopCount(SavedQuery q) => q.Role(QueryAttributes.Options).FirstOrDefault()?.Name1;

    private static string? Select(StringBuilder sql, short options, string? topCount, QueryRow[] outputs)
    {
        if ((options & ~(Star | Distinct | DistinctRow | Top | Percent)) != 0 || ((options & Percent) != 0 && (options & Top) == 0)
            || ((options & Distinct) != 0 && (options & (DistinctRow | Top)) != 0) || ((options & DistinctRow) != 0 && (options & Top) != 0))
        {
            return $"SELECT option bits 0x{options:X4} (a combination not observed)";
        }
        sql.Append("SELECT ");
        if ((options & Distinct) != 0)
        {
            sql.Append("DISTINCT ");
        }
        if ((options & DistinctRow) != 0)
        {
            sql.Append("DISTINCTROW ");
        }
        if ((options & Top) != 0)
        {
            if (topCount is null)
            {
                return "TOP without its count";
            }
            sql.Append("TOP ").Append(topCount).Append((options & Percent) != 0 ? " PERCENT " : " ");
        }
        if ((options & Star) != 0)
        {
            if (outputs.Length != 0)
            {
                return "SELECT * with further outputs (not observed)";
            }
            sql.Append('*');
            return null;
        }
        if (outputs.Length == 0)
        {
            return "a SELECT without outputs";
        }
        for (int i = 0; i < outputs.Length; i++)
        {
            if (outputs[i].Expression is null)
            {
                return "an output row without an expression";
            }
            sql.Append(i == 0 ? "" : ", ").Append(Projection(outputs[i]));
        }
        return null;
    }

    private static string Projection(QueryRow output) => output.Name1 is { } alias ? $"{output.Expression} AS {alias}" : output.Expression!;

    /// <summary>FROM, WHERE, GROUP BY, HAVING, PIVOT, ORDER BY, and optional WITH OWNERACCESS OPTION, then the closing semicolon.</summary>
    private static RenderedSql Tail(SavedQuery q, StringBuilder sql, bool from, QueryFamily family, bool ownerAccess = false)
    {
        if (from)
        {
            if (From(q, sql) is { } fromFailure)
            {
                return RenderedSql.Refuse(fromFailure);
            }
        }
        if (Single(q, QueryAttributes.Where, out string? where) is { } whereFailure)
        {
            return RenderedSql.Refuse(whereFailure);
        }
        if (where is not null)
        {
            sql.Append(Line).Append("WHERE ").Append(where);
        }
        QueryRow[] groups = [.. q.Role(QueryAttributes.GroupBy)];
        if (family == QueryFamily.Crosstab)
        {
            QueryRow[] pivots = [.. q.Role(QueryAttributes.Output).Where(o => o.Flag == Pivot)];
            QueryRow[] grouped = [.. groups.Where(g => g.Flag == CrosstabOutput)];
            if (pivots is not [{ Expression: { } pivot, Name1: null }] || grouped.Length == 0 || groups.Any(g => g.Flag is not (Pivot or CrosstabOutput)))
            {
                return RenderedSql.Refuse("a crosstab other than one pivot over grouped outputs");
            }
            sql.Append(Line).Append("GROUP BY ").AppendJoin(", ", grouped.Select(g => g.Expression));
            if (q.Role(QueryAttributes.Having).Any() || q.Role(QueryAttributes.OrderBy).Any())
            {
                return RenderedSql.Refuse("a crosstab with HAVING or ORDER BY (not observed)");
            }
            sql.Append(Line).Append("PIVOT ").Append(pivot);
        }
        else
        {
            if (groups.Any(g => (g.Flag ?? 0) != 0 || g.Expression is null))
            {
                return RenderedSql.Refuse("a GROUP BY row with flags");
            }
            if (groups.Length > 1)
            {
                return RenderedSql.Refuse("GROUP BY over several terms (its separator is not observed)");
            }
            if (groups.Length == 1)
            {
                sql.Append(Line).Append("GROUP BY ").Append(groups[0].Expression);
            }
            if (Single(q, QueryAttributes.Having, out string? having) is { } havingFailure)
            {
                return RenderedSql.Refuse(havingFailure);
            }
            if (having is not null)
            {
                sql.Append(Line).Append("HAVING ").Append(having);
            }
            if (OrderBy(q, sql) is { } orderFailure)
            {
                return RenderedSql.Refuse(orderFailure);
            }
        }
        if (ownerAccess)
        {
            // r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-view-create-drop-mdb V2: "SELECT ID\r\nFROM T\r\nWITH OWNERACCESS OPTION;\r\n"
            sql.Append(Line).Append("WITH OWNERACCESS OPTION");
        }
        sql.Append(';').Append(Line);
        return RenderedSql.Of(sql.ToString());
    }

    private static string? From(SavedQuery q, StringBuilder sql)
    {
        QueryRow[] sources = [.. q.Role(QueryAttributes.Source)];
        QueryRow[] joins = [.. q.Role(QueryAttributes.Join)];
        if (sources.Length == 0 || sources.Any(s => s.Name1 is null || s.Name2 is not null || s.Expression is not null))
        {
            return "a source with an alias, a qualifier or no name (not observed)";
        }
        sql.Append(Line).Append("FROM ");
        if (joins.Length == 0)
        {
            if (sources.Length > 1)
            {
                return "several sources without a join (not observed)";
            }
            sql.Append(sources[0].Name1);
            return null;
        }
        if (joins is not [{ Name1: { } left, Name2: { } right, Expression: { } on, Flag: >= 1 and <= 3 and var kind }] || sources.Length != 2
            || sources[0].Name1 != left || sources[1].Name1 != right)
        {
            return "a join other than one join of the two sources in source order (not observed)";
        }
        sql.Append(left).Append(kind switch { 1 => " INNER JOIN ", 2 => " LEFT JOIN ", _ => " RIGHT JOIN " }).Append(right).Append(" ON ").Append(on);
        return null;
    }

    private static string? OrderBy(SavedQuery q, StringBuilder sql)
    {
        QueryRow[] terms = [.. q.Role(QueryAttributes.OrderBy)];
        if (terms.Length == 0)
        {
            return null;
        }
        sql.Append(Line).Append("ORDER BY ");
        for (int i = 0; i < terms.Length; i++)
        {
            QueryRow t = terms[i];
            bool descending = t.Name1 is "D" or "d";
            if (t.Expression is null || (t.Name1 is not null && !descending))
            {
                return "an ORDER BY row other than an expression with an optional descending mark";
            }
            bool last = i == terms.Length - 1;
            if (descending && last)
            {
                return "a descending last ORDER BY term (its spelling is not observed)";
            }
            if (!descending && !last)
            {
                return "an ascending ORDER BY term before another (its separator is not observed)";
            }
            sql.Append(t.Expression).Append(descending ? " DESC , " : "");
        }
        return null;
    }

    private static string? Single(SavedQuery q, byte attribute, out string? expression)
    {
        QueryRow[] rows = [.. q.Role(attribute)];
        expression = rows.Length == 1 ? rows[0].Expression : null;
        return rows.Length > 1 || (rows.Length == 1 && rows[0].Expression is null) ? $"attribute {attribute} rows other than one expression" : null;
    }
}
