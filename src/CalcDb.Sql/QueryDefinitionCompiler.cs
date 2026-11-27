using System.Buffers.Binary;
using CalcDb.Catalog;
using CalcDb.Format;

namespace CalcDb.Sql;

/// <summary>Result of compiling SQL text (and optional Connect string) into MSysQueries role rows for CreateQueryDef.</summary>
public readonly record struct QueryDefinitionCompilation(QueryFamily Family, IReadOnlyList<QueryRow>? Rows, SqlFailure? Failure)
{
    public bool Succeeded => Rows is not null && Failure is null;

    public static QueryDefinitionCompilation Ok(QueryFamily family, IReadOnlyList<QueryRow> rows) => new(family, rows, null);

    public static QueryDefinitionCompilation Fail(SqlFailure failure) => new(default, null, failure);
}

/// <summary>
/// Compiles a single-table Access SQL query definition into the exact <see cref="QueryFamily"/> and encounter-ordered
/// <see cref="QueryRow"/> sequence that ACE writes to <c>MSysQueries</c> (II-06 §8; qualified.md <c>R-CAT-1 CAT-Q r-q39</c>;
/// <c>evidence/r-q39-querydef-bytes</c>). Reuses <see cref="SqlParser"/>, <see cref="ActionParser"/>, and
/// <see cref="DdlParser"/> without binding table or column names (<c>r-cat-1b/qd-create-invalid-sql</c>,
/// <c>r-dao-1-err-1/draft-querydef</c>: missing tables and columns succeed at create time; syntax errors fail 3129, 3142,
/// or 3075). Every shape not decided by <c>r-q39</c> is refused by name.
/// </summary>
public static class QueryDefinitionCompiler
{
    private const short TypeMakeTable = 2;
    private const short TypeAppend = 3;
    private const short TypeUpdate = 4;
    private const short TypeDelete = 5;
    private const short TypeCrosstab = 6;
    private const short TypeDataDefinition = 7;
    private const short TypePassThrough = 8;
    private const short TypeUnion = 9;

    private const short OptionStar = 0x0001;
    private const short OptionOwnerAccess = 0x0004;
    private const short OptionUnionDistinct = 0x0003;
    private const short OptionUnionAll = 0x0001;

    private const short AppendValuesFlag = unchecked((short)0x8000);
    private const short CrosstabPivotFlag = 1;
    private const short CrosstabGroupFlag = 2;

    /// <summary>
    /// Compile <paramref name="sql"/> (and optional pass-through <paramref name="connect"/>) into <c>MSysQueries</c> rows.
    /// When <paramref name="isAdoDdl"/> is true (ADO <c>CREATE VIEW</c> / <c>CREATE PROCEDURE</c>,
    /// <c>r-q50-jk44-ddl-vis/ddl-view-create-drop-*</c>, <c>ddl-proc-create-exec-drop-*</c>), the <c>Start (0x00)</c> row
    /// carries <c>Flag = 1</c> instead of DAO's <c>Flag = 0</c>.
    /// </summary>
    public static QueryDefinitionCompilation Compile(string sql, string? connect = null, bool isAdoDdl = false, bool allowOwnerAccess = false)
    {
        if (!string.IsNullOrEmpty(connect))
        {
            return CompilePassThrough(sql, connect);
        }
        if (string.IsNullOrWhiteSpace(sql))
        {
            // r-cat-1b/qd-create-invalid-sql call 37: CreateQueryDef("QEmptySql", "") -> 3129.
            return QueryDefinitionCompilation.Fail(SqlFailure.Ace(SqlPhase.Parse, 3129, SqlParser.InvalidStatement));
        }

        short startFlag = isAdoDdl ? (short)1 : (short)0;
        string lead = LeadKeyword(sql);
        if (lead.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || lead == "(")
        {
            return CompileSelectOrUnion(sql, startFlag, allowOwnerAccess);
        }
        if (lead.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("DELETE", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return CompileAction(sql, startFlag);
        }
        if (lead.Equals("TRANSFORM", StringComparison.OrdinalIgnoreCase))
        {
            return CompileCrosstab(sql);
        }
        if (lead.Equals("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            return CompileDdl(sql);
        }
        if (lead.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("DROP", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("GRANT", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("REVOKE", StringComparison.OrdinalIgnoreCase))
        {
            return Open("NotModelled.QueryDefDdlStatement", $"CreateQueryDef of DDL statement starting with '{lead}' (only CREATE TABLE is recorded in r-q39/ddl-accdb)");
        }
        if (lead.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase))
        {
            return Open("NotModelled.QueryDefProcedure", "CreateQueryDef of a PROCEDURE statement (not recorded in r-q39)");
        }
        // r-cat-1b/qd-create-invalid-sql call 4 ("SELEC ID FROM T;"), r-dao-1-err-1/draft-querydef call 27 ("SELEC ID FROM T"): 3129.
        return QueryDefinitionCompilation.Fail(SqlFailure.Ace(SqlPhase.Parse, 3129, SqlParser.InvalidStatement));
    }

    /// <summary>
    /// Classify the <see cref="QueryFamily"/> of a temporary QueryDef (<c>CreateQueryDef("", sql)</c>) without imposing
    /// saved-query byte-layout restrictions (temporary QueryDefs write no bytes and answer SQL, Type, Parameters,
    /// OpenRecordset and Execute directly over the statement).
    /// </summary>
    public static QueryFamily? ClassifyFamily(string sql, string? connect = null)
    {
        if (!string.IsNullOrEmpty(connect))
        {
            return QueryFamily.PassThrough;
        }
        if (string.IsNullOrWhiteSpace(sql))
        {
            return QueryFamily.Select;
        }
        string lead = LeadKeyword(sql);
        if (lead.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || lead == "(")
        {
            using SelectSyntax? parsed = SqlParser.ParseForDefinition(sql, out _);
            if (parsed is null)
            {
                return QueryFamily.Select;
            }
            if (parsed.IsUnion)
            {
                return QueryFamily.Union;
            }
            return parsed.IntoTarget is not null ? QueryFamily.MakeTable : QueryFamily.Select;
        }
        if (lead.Equals("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return QueryFamily.Update;
        }
        if (lead.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return QueryFamily.Delete;
        }
        if (lead.Equals("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            return QueryFamily.Append;
        }
        if (lead.Equals("TRANSFORM", StringComparison.OrdinalIgnoreCase))
        {
            return QueryFamily.Crosstab;
        }
        if (lead.Equals("CREATE", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
            || lead.Equals("DROP", StringComparison.OrdinalIgnoreCase))
        {
            return QueryFamily.DataDefinition;
        }
        return null;
    }

    private static QueryDefinitionCompilation CompilePassThrough(string sql, string connect)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return Open("NotModelled.PassThroughEmptySql", "CreateQueryDef of a pass-through query with empty SQL (r-q39/passthrough-accdb records non-empty SQL)");
        }
        // r-q39-querydef-bytes/passthrough-accdb: [0x00, 0xFF, 0x01(Flag=8, Name1=connect, Expression=sql)].
        var b = new RowBuilder();
        b.Add(QueryAttributes.Type, flag: TypePassThrough, name1: connect, expression: sql);
        return QueryDefinitionCompilation.Ok(QueryFamily.PassThrough, b.Build());
    }

    private static QueryDefinitionCompilation CompileSelectOrUnion(string sql, short startFlag = 0, bool allowOwnerAccess = false)
    {
        using SelectSyntax? select = SqlParser.ParseForDefinition(sql, out SqlFailure? failure);
        if (select is null)
        {
            return QueryDefinitionCompilation.Fail(failure!);
        }
        if (select.WithOwnerAccess && (!allowOwnerAccess || select.IsUnion || select.IntoTarget is not null))
        {
            return Open("NotModelled.QueryDefOwnerAccess", "CreateQueryDef with WITH OWNERACCESS OPTION (only ADO CREATE VIEW plain SELECT is recorded in r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-view-create-drop-mdb)");
        }
        if (select.IsUnion)
        {
            return CompileUnion(select);
        }
        if (select.IntoTarget is not null)
        {
            return CompileSelectInto(select);
        }
        return CompileSelect(select, startFlag);
    }

    private static QueryDefinitionCompilation CompileSelect(SelectSyntax select, short startFlag = 0)
    {
        if (select.Predicate != SelectPredicate.All)
        {
            return Open("NotModelled.QueryDefSelectPredicate", $"CreateQueryDef with {select.Predicate} (undecided in r-q39)");
        }
        if (select.Top is not null)
        {
            return Open("NotModelled.QueryDefTop", "CreateQueryDef with TOP (undecided in r-q39)");
        }
        if (select.GroupBy.Count > 0)
        {
            return Open("NotModelled.QueryDefGroupBy", "CreateQueryDef with GROUP BY outside a crosstab (undecided in r-q39)");
        }
        if (select.Having is not null)
        {
            return Open("NotModelled.QueryDefHaving", "CreateQueryDef with HAVING (undecided in r-q39)");
        }
        if (ValidateSingleSource(select, out string? tableName) is { } sourceRefusal)
        {
            return sourceRefusal;
        }
        if (ValidateParameters(select.Parameters) is { } paramRefusal)
        {
            return paramRefusal;
        }

        string src = select.Source;
        var b = new RowBuilder(startFlag);
        EmitParameters(b, select.Parameters);

        bool isBareStar = select.Items is [{ Star: true, StarSource: null }];
        if (isBareStar)
        {
            if (select.Parameters.Count > 0 || select.Where is not null || select.OrderBy.Count > 0 || select.WithOwnerAccess)
            {
                return Open("NotModelled.QueryDefStarWithClauses", "CreateQueryDef of SELECT * with PARAMETERS/WHERE/ORDER BY/WITH OWNERACCESS OPTION (only bare SELECT * FROM T is recorded in r-q39/select-star-*)");
            }
            // r-q39-querydef-bytes/select-star-accdb and select-star-mdb: [0x00, 0xFF, 0x05(Name1=T), 0x03(Flag=1)].
            b.Add(QueryAttributes.Source, name1: tableName);
            b.Add(QueryAttributes.Options, flag: OptionStar);
        }
        else
        {
            if (select.Items.Any(static i => i.Star))
            {
                return Open("NotModelled.QueryDefMixedStar", "CreateQueryDef with table.* or mixed * and column expressions (undecided in r-q39)");
            }
            // r-q39-querydef-bytes/select-cols-accdb, select-where-order-*, select-params-*: Output(0x06)* then Source(0x05).
            foreach (SelectItem item in select.Items)
            {
                if (ValidatePlainIdentifier(item.Alias, "output alias") is { } aliasRefusal)
                {
                    return aliasRefusal;
                }
                b.Add(QueryAttributes.Output, flag: 0, name1: item.Alias, expression: item.Expression!.Text(src));
            }
            b.Add(QueryAttributes.Source, name1: tableName);
            if (select.WithOwnerAccess)
            {
                // r-q50-jk44-ddl-vis/ddl-view-create-drop-accdb, ddl-view-create-drop-mdb V2: Options(0x03, Flag=0x0004).
                b.Add(QueryAttributes.Options, flag: OptionOwnerAccess);
            }
        }

        if (select.Where is { } where)
        {
            b.Add(QueryAttributes.Where, expression: where.Text(src));
        }
        if (ValidateOrderBy(select.OrderBy) is { } orderRefusal)
        {
            return orderRefusal;
        }
        foreach (OrderItem order in select.OrderBy)
        {
            // r-q39-querydef-bytes/select-where-order-accdb/mdb: DESC stores Name1 = "d", ASC stores Name1 = null.
            b.Add(QueryAttributes.OrderBy, name1: order.Descending ? "d" : null, expression: order.Expression.Text(src));
        }
        return QueryDefinitionCompilation.Ok(QueryFamily.Select, b.Build());
    }

    private static QueryDefinitionCompilation CompileSelectInto(SelectSyntax select)
    {
        if (select.Parameters.Count > 0)
        {
            return Open("NotModelled.QueryDefActionParameters", "CreateQueryDef of SELECT INTO with PARAMETERS (undecided in r-q39)");
        }
        if (select.Predicate != SelectPredicate.All || select.Top is not null || select.GroupBy.Count > 0 || select.Having is not null || select.OrderBy.Count > 0)
        {
            return Open("NotModelled.QueryDefSelectIntoClause", "CreateQueryDef of SELECT INTO with DISTINCT/TOP/GROUP BY/HAVING/ORDER BY (undecided in r-q39)");
        }
        if (select.Items.Any(static i => i.Star))
        {
            return Open("NotModelled.QueryDefSelectIntoStar", "CreateQueryDef of SELECT * INTO (undecided in r-q39)");
        }
        if (ValidateSingleSource(select, out string? tableName) is { } sourceRefusal)
        {
            return sourceRefusal;
        }
        if (ValidatePlainIdentifier(select.IntoTarget, "INTO target table") is { } intoRefusal)
        {
            return intoRefusal;
        }

        // r-q39-querydef-bytes/action-select-into-accdb: [0x00, 0xFF, 0x06*, 0x01(Flag=2, Name1=target), 0x05(Name1=src), 0x08?].
        string src = select.Source;
        var b = new RowBuilder();
        foreach (SelectItem item in select.Items)
        {
            if (ValidatePlainIdentifier(item.Alias, "output alias") is { } aliasRefusal)
            {
                return aliasRefusal;
            }
            b.Add(QueryAttributes.Output, flag: 0, name1: item.Alias, expression: item.Expression!.Text(src));
        }
        b.Add(QueryAttributes.Type, flag: TypeMakeTable, name1: select.IntoTarget);
        b.Add(QueryAttributes.Source, name1: tableName);
        if (select.Where is { } where)
        {
            b.Add(QueryAttributes.Where, expression: where.Text(src));
        }
        return QueryDefinitionCompilation.Ok(QueryFamily.MakeTable, b.Build());
    }

    private static QueryDefinitionCompilation CompileUnion(SelectSyntax select)
    {
        if (select.Parameters.Count > 0)
        {
            return Open("NotModelled.QueryDefUnionParameters", "CreateQueryDef of UNION with PARAMETERS (undecided in r-q39)");
        }
        if (select.UnionOrderBy.Count > 0)
        {
            return Open("NotModelled.QueryDefUnionOrderBy", "CreateQueryDef of UNION with ORDER BY (undecided in r-q39)");
        }
        if (select.Unions.Count != 1)
        {
            return Open("NotModelled.QueryDefMultiUnion", $"CreateQueryDef of a {select.Unions.Count + 1}-arm UNION (only 2-arm UNION is recorded in r-q39)");
        }
        UnionArm second = select.Unions[0];
        if (select.ParenthesizedArm || second.Select.ParenthesizedArm)
        {
            return Open("NotModelled.QueryDefParenthesizedUnion", "CreateQueryDef of parenthesized UNION arms (undecided in r-q39)");
        }
        if (ValidateUnionArm(select) is { } firstRefusal)
        {
            return firstRefusal;
        }
        if (ValidateUnionArm(second.Select) is { } secondRefusal)
        {
            return secondRefusal;
        }

        // r-q39-querydef-bytes/union-accdb & union-all-accdb:
        // [0x00, 0xFF, 0x05(Name2="X7YZ_____1", Expr=arm1+" "), 0x01(Flag=9), 0x05(Name2="X7YZ_____2", Expr=arm2), 0x03(Flag=3|1)].
        string arm1 = select.Source[select.ArmStart..select.ArmEnd].Trim();
        string arm2 = select.Source[second.Select.ArmStart..second.Select.ArmEnd].Trim();
        var b = new RowBuilder();
        b.Add(QueryAttributes.Source, name2: "X7YZ_____1", expression: arm1 + " ");
        b.Add(QueryAttributes.Type, flag: TypeUnion);
        b.Add(QueryAttributes.Source, name2: "X7YZ_____2", expression: arm2);
        b.Add(QueryAttributes.Options, flag: second.All ? OptionUnionAll : OptionUnionDistinct);
        return QueryDefinitionCompilation.Ok(QueryFamily.Union, b.Build());
    }

    private static QueryDefinitionCompilation? ValidateUnionArm(SelectSyntax arm)
    {
        if (arm.Predicate != SelectPredicate.All || arm.Top is not null || arm.GroupBy.Count > 0 || arm.Having is not null || arm.OrderBy.Count > 0 || arm.IntoTarget is not null)
        {
            return Open("NotModelled.QueryDefUnionArmClause", "CreateQueryDef of a UNION arm with DISTINCT/TOP/GROUP BY/HAVING/ORDER BY (undecided in r-q39)");
        }
        return ValidateSingleSource(arm, out _);
    }

    private static QueryDefinitionCompilation CompileAction(string sql, short startFlag = 0)
    {
        using ActionSyntax? action = ActionParser.Parse(sql, out SqlFailure? failure);
        if (action is null)
        {
            return QueryDefinitionCompilation.Fail(failure!);
        }
        if (action.Parameters.Count > 0)
        {
            if (startFlag == 0 || action.Kind != ActionKind.Update)
            {
                return Open("NotModelled.QueryDefActionParameters", $"CreateQueryDef of {action.Kind} with PARAMETERS (only ADO CREATE PROCEDURE UPDATE is recorded in r-q50-jk44-ddl-vis/ddl-proc-create-exec-drop-*)");
            }
            if (ValidateParameters(action.Parameters) is { } actionParamRefusal)
            {
                return actionParamRefusal;
            }
        }
        if (action.WithOwnerAccess)
        {
            return Open("NotModelled.QueryDefOwnerAccess", "CreateQueryDef with WITH OWNERACCESS OPTION (undecided in r-q39)");
        }
        if (action.TargetAlias is not null)
        {
            return Open("NotModelled.QueryDefTargetAlias", $"CreateQueryDef of {action.Kind} with aliased target table (undecided in r-q39)");
        }
        if (ValidatePlainIdentifier(action.TargetName, "target table") is { } targetRefusal)
        {
            return targetRefusal;
        }

        string src = action.Source;
        var b = new RowBuilder(startFlag);
        EmitParameters(b, action.Parameters);
        switch (action.Kind)
        {
            case ActionKind.Update:
                if (action.Assignments.Any(static a => a.Qualifier is not null))
                {
                    return Open("NotModelled.QueryDefQualifiedSet", "CreateQueryDef of UPDATE with table-qualified SET target (undecided in r-q39)");
                }
                // r-q39-querydef-bytes/action-update-accdb & action-update-mdb: [0x00, 0xFF, 0x01(4), 0x05(T), 0x06(0, Name2=col, Expr=val)*, 0x08?].
                b.Add(QueryAttributes.Type, flag: TypeUpdate);
                b.Add(QueryAttributes.Source, name1: action.TargetName);
                foreach (SetAssignment a in action.Assignments)
                {
                    if (ValidatePlainIdentifier(a.Column, "SET column") is { } colRefusal)
                    {
                        return colRefusal;
                    }
                    b.Add(QueryAttributes.Output, flag: 0, name2: a.Column, expression: a.Value.Text(src));
                }
                if (action.Where is { } updWhere)
                {
                    b.Add(QueryAttributes.Where, expression: updWhere.Text(src));
                }
                return QueryDefinitionCompilation.Ok(QueryFamily.Update, b.Build());

            case ActionKind.Delete:
                // r-q39-querydef-bytes/action-delete-accdb: [0x00, 0xFF, 0x01(5), 0x05(T), 0x08?].
                b.Add(QueryAttributes.Type, flag: TypeDelete);
                b.Add(QueryAttributes.Source, name1: action.TargetName);
                if (action.Where is { } delWhere)
                {
                    b.Add(QueryAttributes.Where, expression: delWhere.Text(src));
                }
                return QueryDefinitionCompilation.Ok(QueryFamily.Delete, b.Build());

            case ActionKind.InsertValues:
                if (action.TargetColumns is null || action.TargetColumns.Count != action.Values.Count)
                {
                    return Open("NotModelled.QueryDefInsertValuesShape", "CreateQueryDef of INSERT INTO ... VALUES without an explicit matching column list (undecided in r-q39)");
                }
                // r-q39-querydef-bytes/action-insert-values-accdb: [0x00, 0xFF, 0x01(3, Name1=T), 0x06(0x8000, Name2=col, Expr=val)*].
                b.Add(QueryAttributes.Type, flag: TypeAppend, name1: action.TargetName);
                for (int i = 0; i < action.TargetColumns.Count; i++)
                {
                    if (ValidatePlainIdentifier(action.TargetColumns[i], "INSERT target column") is { } colRefusal)
                    {
                        return colRefusal;
                    }
                    b.Add(QueryAttributes.Output, flag: AppendValuesFlag, name2: action.TargetColumns[i], expression: action.Values[i].Text(src));
                }
                return QueryDefinitionCompilation.Ok(QueryFamily.Append, b.Build());

            case ActionKind.InsertSelect:
                if (action.TargetColumns is null || action.SourceSelect is not { } sourceSelect || !action.SourceSelectHasFrom)
                {
                    return Open("NotModelled.QueryDefInsertSelectShape", "CreateQueryDef of INSERT INTO ... SELECT without explicit columns and FROM (undecided in r-q39)");
                }
                if (sourceSelect.Predicate != SelectPredicate.All || sourceSelect.Top is not null || sourceSelect.GroupBy.Count > 0
                    || sourceSelect.Having is not null || sourceSelect.OrderBy.Count > 0 || sourceSelect.Items.Any(static i => i.Star))
                {
                    return Open("NotModelled.QueryDefInsertSelectClause", "CreateQueryDef of INSERT INTO ... SELECT with DISTINCT/TOP/GROUP BY/HAVING/ORDER BY/* (undecided in r-q39)");
                }
                if (sourceSelect.Items.Count != action.TargetColumns.Count)
                {
                    return Open("NotModelled.QueryDefInsertSelectArity", "CreateQueryDef of INSERT INTO ... SELECT with mismatched column and expression counts (undecided in r-q39)");
                }
                if (ValidateSingleSource(sourceSelect, out string? sourceTable) is { } insSourceRefusal)
                {
                    return insSourceRefusal;
                }
                // r-q39-querydef-bytes/action-insert-select-accdb: [0x00, 0xFF, 0x01(3, Name1=target), 0x06(0, Name2=col, Expr=expr)*, 0x05(src), 0x08?].
                b.Add(QueryAttributes.Type, flag: TypeAppend, name1: action.TargetName);
                for (int i = 0; i < action.TargetColumns.Count; i++)
                {
                    if (ValidatePlainIdentifier(action.TargetColumns[i], "INSERT target column") is { } colRefusal)
                    {
                        return colRefusal;
                    }
                    b.Add(QueryAttributes.Output, flag: 0, name2: action.TargetColumns[i], expression: sourceSelect.Items[i].Expression!.Text(src));
                }
                b.Add(QueryAttributes.Source, name1: sourceTable);
                if (sourceSelect.Where is { } insWhere)
                {
                    b.Add(QueryAttributes.Where, expression: insWhere.Text(src));
                }
                return QueryDefinitionCompilation.Ok(QueryFamily.Append, b.Build());

            default:
                return Open("NotModelled.QueryDefActionKind", $"CreateQueryDef of action kind {action.Kind}");
        }
    }

    private static QueryDefinitionCompilation CompileCrosstab(string sql)
    {
        using CrosstabSyntax? xtab = SqlParser.ParseCrosstab(sql, out SqlFailure? failure);
        if (xtab is null)
        {
            return QueryDefinitionCompilation.Fail(failure!);
        }
        SelectSyntax select = xtab.Select;
        if (xtab.HasPivotIn)
        {
            return Open("NotModelled.QueryDefPivotIn", "CreateQueryDef of a crosstab with PIVOT ... IN (...) (undecided in r-q39)");
        }
        if (select.Parameters.Count > 0)
        {
            return Open("NotModelled.QueryDefCrosstabParameters", "CreateQueryDef of a crosstab with PARAMETERS (undecided in r-q39)");
        }
        if (select.WithOwnerAccess)
        {
            return Open("NotModelled.QueryDefOwnerAccess", "CreateQueryDef of a crosstab with WITH OWNERACCESS OPTION (undecided in r-q39)");
        }
        if (select.Predicate != SelectPredicate.All || select.Top is not null || select.Having is not null || select.OrderBy.Count > 0 || select.GroupBy.Count != 1)
        {
            return Open("NotModelled.QueryDefCrosstabClause", "CreateQueryDef of a crosstab with DISTINCT/TOP/HAVING/ORDER BY or GROUP BY count other than 1 (only single-term GROUP BY is recorded in r-q39/crosstab-accdb)");
        }
        if (string.IsNullOrEmpty(xtab.TransformAlias))
        {
            return Open("NotModelled.QueryDefCrosstabUnaliasedTransform", "CreateQueryDef of a crosstab without an AS alias on TRANSFORM (only aliased TRANSFORM is recorded in r-q39/crosstab-accdb)");
        }
        if (ValidatePlainIdentifier(xtab.TransformAlias, "TRANSFORM alias") is { } xtabAliasRefusal)
        {
            return xtabAliasRefusal;
        }
        if (select.Items.Any(static i => i.Star))
        {
            return Open("NotModelled.QueryDefCrosstabStar", "CreateQueryDef of a crosstab with * (undecided in r-q39)");
        }
        if (ValidateSingleSource(select, out string? tableName) is { } sourceRefusal)
        {
            return sourceRefusal;
        }

        // r-q39-querydef-bytes/crosstab-accdb:
        // [0x00, 0xFF, 0x01(6), 0x06(0, Name1=alias, Expr=transform), 0x06(2, Expr=rowHeader)*, 0x05(T), 0x08?, 0x09(2, Expr=group), 0x09(1, Expr=pivot), 0x06(1, Expr=pivot)].
        string src = select.Source;
        var b = new RowBuilder();
        b.Add(QueryAttributes.Type, flag: TypeCrosstab);
        b.Add(QueryAttributes.Output, flag: 0, name1: xtab.TransformAlias, expression: xtab.Transform.Text(src));
        foreach (SelectItem item in select.Items)
        {
            if (ValidatePlainIdentifier(item.Alias, "crosstab row-header alias") is { } itemAliasRefusal)
            {
                return itemAliasRefusal;
            }
            b.Add(QueryAttributes.Output, flag: CrosstabGroupFlag, name1: item.Alias, expression: item.Expression!.Text(src));
        }
        b.Add(QueryAttributes.Source, name1: tableName);
        if (select.Where is { } where)
        {
            b.Add(QueryAttributes.Where, expression: where.Text(src));
        }
        foreach (Fragment g in select.GroupBy)
        {
            b.Add(QueryAttributes.GroupBy, flag: CrosstabGroupFlag, expression: g.Text(src));
        }
        string pivotText = xtab.Pivot.Text(src);
        b.Add(QueryAttributes.GroupBy, flag: CrosstabPivotFlag, expression: pivotText);
        b.Add(QueryAttributes.Output, flag: CrosstabPivotFlag, expression: pivotText);
        return QueryDefinitionCompilation.Ok(QueryFamily.Crosstab, b.Build());
    }

    private static QueryDefinitionCompilation CompileDdl(string sql)
    {
        DdlStatement? ddl = DdlParser.Parse(sql, out SqlFailure? failure);
        if (ddl is null)
        {
            return QueryDefinitionCompilation.Fail(failure!);
        }
        if (ddl is not CreateTableStatement)
        {
            return Open("NotModelled.QueryDefDdlStatement", $"CreateQueryDef of DDL {ddl.GetType().Name} (only CREATE TABLE is recorded in r-q39/ddl-accdb)");
        }
        // r-q39-querydef-bytes/ddl-accdb: [0x00, 0xFF, 0x01(Flag=7, Expression=sql)].
        var b = new RowBuilder();
        b.Add(QueryAttributes.Type, flag: TypeDataDefinition, expression: sql);
        return QueryDefinitionCompilation.Ok(QueryFamily.DataDefinition, b.Build());
    }

    private static QueryDefinitionCompilation? ValidateSingleSource(SelectSyntax select, out string? tableName)
    {
        tableName = null;
        if (select.From.Count != 1)
        {
            return Open("NotModelled.QueryDefMultipleSources", $"CreateQueryDef with {select.From.Count} FROM sources (only one source is recorded in r-q39)");
        }
        switch (select.From[0])
        {
            case NamedSource { Alias: null } named:
                if (ValidatePlainIdentifier(named.Name, "FROM source") is { } nameRefusal)
                {
                    return nameRefusal;
                }
                tableName = named.Name;
                return null;
            case NamedSource aliased:
                return Open("NotModelled.QueryDefSourceAlias", $"CreateQueryDef with aliased FROM source '{aliased.Name} AS {aliased.Alias}' (undecided in r-q39)");
            case JoinSource:
                return Open("NotModelled.QueryDefJoin", "CreateQueryDef with a JOIN (undecided in r-q39)");
            case SubquerySource:
                return Open("NotModelled.QueryDefSubquery", "CreateQueryDef with a subquery in FROM (undecided in r-q39)");
            default:
                return Open("NotModelled.QueryDefSource", "CreateQueryDef with an unrecorded FROM source shape");
        }
    }

    private static QueryDefinitionCompilation? ValidateOrderBy(IReadOnlyList<OrderItem> orderBy)
    {
        // Only single ASC (r-cat-1/querydefs QOneOrdered, r-cat-1b/qd-view-select) and two-term DESC, ASC
        // (r-q39-querydef-bytes/select-where-order-accdb/mdb, r-cat-1/querydefs QOrder) are recorded and rendered by QuerySql.
        return orderBy.Count switch
        {
            0 => null,
            1 when !orderBy[0].Descending => null,
            2 when orderBy[0].Descending && !orderBy[1].Descending => null,
            _ => Open("NotModelled.QueryDefOrderByShape", "CreateQueryDef with an ORDER BY shape other than single ASC or two-term DESC, ASC (not recorded in r-q39 / QuerySql)"),
        };
    }

    private static QueryDefinitionCompilation? ValidateParameters(IReadOnlyList<ParameterDeclaration> parameters)
    {
        bool seenText = false;
        foreach (ParameterDeclaration p in parameters)
        {
            if (ValidatePlainIdentifier(p.Name, "parameter name") is { } nameRefusal)
            {
                return nameRefusal;
            }
            if (p.DaoType == NativeTypes.Long && p.TextSize is null)
            {
                if (seenText)
                {
                    return Open("NotModelled.QueryDefParameterOrder", "CreateQueryDef PARAMETERS with Long after Text(n) (LvExtra stickiness after Text(n) is only recorded for non-parameter rows in r-q39/select-params-*)");
                }
                continue;
            }
            if (p.DaoType == NativeTypes.Text && p.TextSize is > 0)
            {
                seenText = true;
                continue;
            }
            return Open("NotModelled.QueryDefParameterType", $"CreateQueryDef PARAMETERS type {p.DaoType} (size {p.TextSize?.ToString() ?? "none"}): only Long and Text(n) are recorded in r-q39/select-params-*");
        }
        return null;
    }

    private static readonly HashSet<string> ReservedSqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "ORDER", "GROUP", "BY", "HAVING", "AS", "INTO", "SET", "VALUES",
        "UNION", "ALL", "DISTINCT", "DISTINCTROW", "TOP", "PARAMETERS", "TRANSFORM", "PIVOT", "IN",
        "ON", "JOIN", "INNER", "LEFT", "RIGHT", "UPDATE", "DELETE", "INSERT", "CREATE", "ALTER",
        "DROP", "TABLE", "WITH", "AND", "OR", "NOT", "IS", "NULL", "LIKE", "BETWEEN", "TRUE", "FALSE",
    };

    private static QueryDefinitionCompilation? ValidatePlainIdentifier(string? name, string role)
    {
        if (name is null)
        {
            return null;
        }
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || ReservedSqlKeywords.Contains(name))
        {
            return Open("NotModelled.QueryDefDelimitedIdentifier", $"CreateQueryDef {role} '{name}' requires delimiter bracketing (MSysQueries Name1/Name2 bracketing is not recorded in r-q39)");
        }
        foreach (char c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
            {
                return Open("NotModelled.QueryDefDelimitedIdentifier", $"CreateQueryDef {role} '{name}' requires delimiter bracketing (MSysQueries Name1/Name2 bracketing is not recorded in r-q39)");
            }
        }
        return null;
    }

    private static void EmitParameters(RowBuilder b, IReadOnlyList<ParameterDeclaration> parameters)
    {
        foreach (ParameterDeclaration p in parameters)
        {
            b.AddParameter(p.Name, (short)p.DaoType, p.TextSize);
        }
    }

    private static string LeadKeyword(string sql)
    {
        int nul = sql.IndexOf('\0', StringComparison.Ordinal);
        ReadOnlySpan<char> span = (nul >= 0 ? sql.AsSpan(0, nul) : sql.AsSpan()).TrimStart();
        if (StartsWithKeyword(span, "PARAMETERS"))
        {
            int semi = span.IndexOf(';');
            if (semi < 0)
            {
                // Incomplete PARAMETERS clause: let SqlParser report ACE's 3139.
                return "SELECT";
            }
            span = span[(semi + 1)..].TrimStart();
        }
        if (span.Length == 0)
        {
            return "";
        }
        if (span[0] == '(')
        {
            return "(";
        }
        int end = 0;
        while (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
        {
            end++;
        }
        return span[..end].ToString();
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> text, string word) =>
        text.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (text.Length == word.Length || !(char.IsLetterOrDigit(text[word.Length]) || text[word.Length] == '_'));

    private static QueryDefinitionCompilation Open(string name, string detail) =>
        QueryDefinitionCompilation.Fail(SqlFailure.Open(SqlPhase.Parse, name, detail));

    private sealed class RowBuilder
    {
        private readonly List<QueryRow> _rows = [];
        private readonly Dictionary<byte, int> _ordinals = [];
        private bool _lvExtraStickyZero;

        public RowBuilder(short startFlag = 0)
        {
            // Every saved query begins with Start (0x00, Flag=0 for DAO / Flag=1 for ADO VIEW/PROCEDURE) followed by End (0xFF, Flag=null).
            Add(QueryAttributes.Start, flag: startFlag);
            Add(QueryAttributes.End);
        }

        public void AddParameter(string name, short nativeType, int? textSize)
        {
            Add(QueryAttributes.Parameter, flag: nativeType, lvExtra: textSize, name1: name, overrideSticky: true);
            if (textSize is not null)
            {
                // r-q39-querydef-bytes/select-params-accdb & select-params-mdb: after a Text(n) parameter sets LvExtra,
                // every later MSysQueries row of the same query stores LvExtra = 0 instead of Null.
                _lvExtraStickyZero = true;
            }
        }

        public void Add(byte attribute, short? flag = null, int? lvExtra = null, string? name1 = null, string? name2 = null, string? expression = null, bool overrideSticky = false)
        {
            int next = _ordinals.TryGetValue(attribute, out int prev) ? prev + 1 : 1;
            _ordinals[attribute] = next;
            byte[] order = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(order, next);
            int? storedLvExtra = overrideSticky ? lvExtra : (lvExtra ?? (_lvExtraStickyZero ? 0 : null));
            _rows.Add(new QueryRow(default, 0, attribute, expression, flag, storedLvExtra, name1, name2, order));
        }

        public IReadOnlyList<QueryRow> Build() => _rows;
    }
}
