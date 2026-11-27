using CalcDb.Catalog;
using CalcDb.Expressions;
using CalcDb.Format;
using CalcDb.Values.Scalar;

namespace CalcDb.Sql;

/// <summary>One bound <c>SET column = expression</c> assignment of an <c>UPDATE</c> action (II-11 §24).</summary>
public sealed record BoundAssignment(ColumnDescriptor Column, BoundExpression Value, Fragment Syntax);

/// <summary>
/// A bound single-table action plan (W-07/actions; II-11 §§22–28): target table, destination columns, bound RHS/VALUES/WHERE
/// expressions, optional bound source <see cref="BoundSelect"/>, and the statement's parameter list (declared first, then
/// implicit in order of first use, sharing the same <see cref="QueryParameter"/> contract as <see cref="BoundSelect"/>).
/// </summary>
public sealed class BoundAction : IDisposable
{
    private readonly ActionSyntax _syntax;
    private readonly List<BoundExpression> _ownedExpressions = [];

    private BoundAction(
        ActionSyntax syntax,
        TableDefinition targetTable,
        ExpressionContext context,
        List<QueryParameter> parameters,
        SqlFailure? parametersFailure)
    {
        _syntax = syntax;
        TargetTable = targetTable;
        Context = context;
        ParameterList = parameters;
        ParametersFailure = parametersFailure;
    }

    public ActionKind Kind => _syntax.Kind;

    public string Source => _syntax.Source;

    public string TargetName => _syntax.TargetName;

    public TableDefinition TargetTable { get; }

    public ExpressionContext Context { get; }

    /// <summary>Whether <c>WITH OWNERACCESS OPTION</c> was declared (R-SQL-2 SQL-OWNERACCESS).</summary>
    public bool WithOwnerAccess => _syntax.WithOwnerAccess;

    internal List<QueryParameter> ParameterList { get; }

    public IReadOnlyList<QueryParameter> Parameters => ParameterList;

    /// <summary>A deferred parameter metadata failure (e.g., 3060 conflicting declared types, or >=255 parameters).</summary>
    public SqlFailure? ParametersFailure { get; private set; }

    /// <summary>Destination columns of <c>INSERT INTO</c>, aligned with <see cref="InsertValues"/> or <see cref="SourceSelect"/>.</summary>
    public IReadOnlyList<ColumnDescriptor> InsertColumns => _insertColumns;

    /// <summary>Bound value expressions of <c>INSERT INTO … VALUES</c> or a FROM-less <c>INSERT INTO … SELECT</c>.</summary>
    public IReadOnlyList<BoundExpression> InsertValues => _insertValues;

    internal IReadOnlyList<Fragment> InsertValueFragments => _insertValueFragments;

    /// <summary>Bound source SELECT of <c>INSERT INTO … SELECT … FROM …</c>; null for VALUES or FROM-less SELECT.</summary>
    public BoundSelect? SourceSelect { get; private set; }

    /// <summary>Bound SET assignments of <c>UPDATE</c>, in textual order.</summary>
    public IReadOnlyList<BoundAssignment> Assignments => _assignments;

    /// <summary>Bound WHERE predicate of <c>UPDATE</c> or <c>DELETE</c>, or null.</summary>
    public BoundExpression? Where { get; private set; }

    /// <summary>Target table fields for <c>SELECT ... INTO</c> (R-SQL-2 SQL-ACT-MAKE; r-q52-ddl-make-domain/maketable-*).</summary>
    public IReadOnlyList<NewField> MakeTableFields => _makeTableFields;

    /// <summary>Whether <see cref="MakeTableFields"/> contains duplicate column names (fails 3191 via SchemaWriter rollback).</summary>
    public bool HasDuplicateMakeTableField { get; private set; }

    internal Fragment? WhereSyntax => _syntax.Where;

    private readonly List<ColumnDescriptor> _insertColumns = [];
    private readonly List<BoundExpression> _insertValues = [];
    private readonly List<Fragment> _insertValueFragments = [];
    private readonly List<BoundAssignment> _assignments = [];
    private readonly List<NewField> _makeTableFields = [];

    /// <summary>Parse and bind a single-table action statement.</summary>
    public static BoundAction? Prepare(string sql, ISqlDatabase database, out SqlFailure? failure)
    {
        ActionSyntax? syntax = ActionParser.Parse(sql, out failure);
        return syntax is null ? null : Bind(syntax, database, out failure);
    }

    /// <summary>Bind a parsed action statement, taking ownership of <paramref name="syntax"/>.</summary>
    public static BoundAction? Bind(ActionSyntax syntax, ISqlDatabase database, out SqlFailure? failure)
    {
        if (syntax.Kind == ActionKind.MakeTable)
        {
            return BindMakeTable(syntax, database, out failure);
        }
        if (database.IsQuery(syntax.TargetName))
        {
            // Updatable saved-query targets (e.g., INSERT INTO QT15, act-ins-mapping) are outside single-table stretch 1.
            syntax.Dispose();
            failure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.QueryTargetAction", $"action target '{syntax.TargetName}' is a saved query (single-table base targets only in stretch 1)");
            return null;
        }
        TableDefinition? targetTable = database.Table(syntax.TargetName);
        if (targetTable is null)
        {
            string name = syntax.TargetName;
            ActionKind kind = syntax.Kind;
            syntax.Dispose();
            // r-def-expr-sql/sql-insert-values-forms: missing INSERT target -> 3192.
            // r-sql-2-3/act-records-affected, r-def-expr-sql/sql-delete-forms: missing UPDATE/DELETE target -> 3078.
            failure = kind is ActionKind.InsertValues or ActionKind.InsertSelect
                ? SqlFailure.Ace(SqlPhase.Bind, 3192, SqlMessages.NoOutputTable(name))
                : SqlFailure.Ace(SqlPhase.Bind, 3078, SqlMessages.NoInputTable(name));
            return null;
        }

        var parameters = new List<QueryParameter>();
        SqlFailure? parametersFailure = null;
        BoundSelect.DeclareParameters(syntax.Parameters, parameters, ref parametersFailure);

        var bound = new BoundAction(syntax, targetTable, database.Context, parameters, parametersFailure);
        failure = syntax.Kind switch
        {
            ActionKind.InsertValues => bound.BindInsertValues(),
            ActionKind.InsertSelect => bound.BindInsertSelect(database),
            ActionKind.Update => bound.BindUpdate(),
            ActionKind.Delete => bound.BindDelete(),
            _ => SqlFailure.Open(SqlPhase.Bind, "NotModelled.ActionKind", syntax.Kind.ToString()),
        };
        if (failure is not null)
        {
            bound.Dispose();
            return null;
        }
        if (bound.ParameterList.Count >= 255 && bound.ParametersFailure is null)
        {
            bound.ParametersFailure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.ParameterCount", $"{bound.ParameterList.Count} parameters: Parameters.Count fails from 255 used parameters on, unexplained");
        }
        return bound;
    }

    private static BoundAction? BindMakeTable(ActionSyntax syntax, ISqlDatabase database, out SqlFailure? failure)
    {
        if (database is ISqlActionDatabase actionDb && actionDb.Workspace.Depth > 0)
        {
            syntax.Dispose();
            failure = SqlFailure.Open(SqlPhase.Execute, "NotModelled.TransactionalDdlOverlay", "SELECT ... INTO inside an active Workspace transaction is not yet visible to subsequent catalog reads");
            return null;
        }
        // r-q52-ddl-make-domain/maketable-errors-dao-accdb, maketable-errors-dao-mdb call 27: invalid target name [Bad.Name] -> 3125.
        if (!SchemaWriter.IsValidName(syntax.TargetName))
        {
            string bad = syntax.TargetName;
            syntax.Dispose();
            failure = SqlFailure.Ace(SqlPhase.Bind, 3125, SqlMessages.InvalidObjectName(bad));
            return null;
        }
        // r-q52-ddl-make-domain/maketable-errors-dao-accdb, maketable-errors-dao-mdb calls 7, 12; maketable-ado-accdb, maketable-ado-mdb call 57:
        // existing target table (exact or case-insensitive) -> 3010 with statement spelling.
        if (database.Table(syntax.TargetName) is not null)
        {
            string existing = syntax.TargetName;
            syntax.Dispose();
            failure = SqlFailure.Ace(SqlPhase.Bind, 3010, SqlMessages.TableAlreadyExists(existing));
            return null;
        }
        if (database.IsQuery(syntax.TargetName))
        {
            string qname = syntax.TargetName;
            syntax.Dispose();
            failure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.MakeTableTargetIsQuery", $"SELECT ... INTO target '{qname}' collides with a saved query name (not recorded)");
            return null;
        }

        var parameters = new List<QueryParameter>();
        SqlFailure? parametersFailure = null;
        BoundSelect.DeclareParameters(syntax.Parameters, parameters, ref parametersFailure);

        var bound = new BoundAction(syntax, targetTable: null!, database.Context, parameters, parametersFailure);
        BoundSelect? boundSelect = BoundSelect.BindWithParameters(
            syntax.SourceSelect!,
            database,
            parameters,
            out SqlFailure? sf,
            allowDuplicateOutputNames: true);
        if (boundSelect is null)
        {
            bound.Dispose();
            failure = sf;
            return null;
        }
        bound.SourceSelect = boundSelect;
        if (boundSelect.ParametersFailure is { } pf)
        {
            bound.ParametersFailure ??= pf;
        }

        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ResultColumn col in boundSelect.Columns)
        {
            if (!SchemaWriter.IsValidName(col.Name))
            {
                bound.Dispose();
                failure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.MakeTableInvalidColumnName", $"SELECT ... INTO invalid output column name '{col.Name}' (only invalid target table names are recorded in r-q52-ddl-make-domain/maketable-errors-dao-accdb)");
                return null;
            }
            if (!seenNames.Add(col.Name))
            {
                bound.HasDuplicateMakeTableField = true;
            }
            if (MapMakeTableField(col, out SqlFailure? fieldFail) is not { } field)
            {
                bound.Dispose();
                failure = fieldFail!;
                return null;
            }
            bound._makeTableFields.Add(field);
        }

        if (bound.ParameterList.Count >= 255 && bound.ParametersFailure is null)
        {
            bound.ParametersFailure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.ParameterCount", $"{bound.ParameterList.Count} parameters: Parameters.Count fails from 255 used parameters on, unexplained");
        }
        failure = null;
        return bound;
    }

    /// <summary>
    /// Map a bound <see cref="ResultColumn"/> to a <see cref="NewField"/> for <c>SELECT ... INTO</c>:
    /// direct base columns preserve source type/size/fixedness and <c>AutoIncr</c> (r-q52-ddl-make-domain/maketable-basic-dao-*,
    /// maketable-ado-*, r-q47/bi-select-into-accdb, r-q49/dt2-select-into-accdb);
    /// computed expressions and aggregates use fixed layout for 1–2 byte types (Boolean/Byte/Integer) and variable layout
    /// (<c>Attributes=2</c>) for 4+ byte types (Long/Currency/Single/Double/DateTime/Decimal/BigInt/Text(255)/VarBinary(510)),
    /// with bare <c>Null</c> becoming <c>VarBinary(510)</c> (r-q52-ddl-make-domain/maketable-expr-agg-dao-*,
    /// r-q47/meta-select-into-funcs-accdb).
    /// </summary>
    private static NewField? MapMakeTableField(ResultColumn col, out SqlFailure? failure)
    {
        failure = null;
        if (col.Origin is ColumnOrigin.BaseColumn or ColumnOrigin.GroupKey
            && !col.IsGroupExpressionKey
            && col.SourceColumn is { Column: { } src })
        {
            if (src.IsAutoNumber && src.TypeCode == NativeTypes.Long)
            {
                return new NewField(col.Name, NewFieldType.Counter);
            }
            return src.TypeCode switch
            {
                NativeTypes.Boolean => new NewField(col.Name, NewFieldType.YesNo),
                NativeTypes.Byte => new NewField(col.Name, NewFieldType.Byte),
                NativeTypes.Integer => new NewField(col.Name, NewFieldType.Short),
                NativeTypes.Long => new NewField(col.Name, NewFieldType.Long),
                NativeTypes.Currency => new NewField(col.Name, NewFieldType.Currency),
                NativeTypes.Single => new NewField(col.Name, NewFieldType.Single),
                NativeTypes.Double => new NewField(col.Name, NewFieldType.Double),
                NativeTypes.DateTime => new NewField(col.Name, NewFieldType.DateTime),
                NativeTypes.Text => new NewField(col.Name, NewFieldType.Text, Size: Math.Clamp(src.Length / 2, 1, 255)),
                NativeTypes.Memo => new NewField(col.Name, NewFieldType.Memo),
                NativeTypes.Ole => new NewField(col.Name, NewFieldType.Ole),
                NativeTypes.Binary => src.IsFixed
                    ? new NewField(col.Name, NewFieldType.Binary, Size: src.Length)
                    : new NewField(col.Name, NewFieldType.VarBinary, Size: src.Length),
                NativeTypes.Numeric => new NewField(col.Name, NewFieldType.Decimal, Precision: src.NumericPrecision, Scale: src.NumericScale),
                NativeTypes.BigInt => new NewField(col.Name, NewFieldType.BigInt),
                NativeTypes.ExtendedDateTime => new NewField(col.Name, NewFieldType.DateTime2),
                _ => FailMakeTableField(col, $"source column native type 0x{src.TypeCode:X2}", out failure),
            };
        }

        if (col.TypeRule == ColumnTypeRule.NullAtOpen && col.FieldType is null)
        {
            // r-q52-ddl-make-domain/maketable-expr-agg-dao-accdb, maketable-expr-agg-dao-mdb: Null AS NullCol -> Type=9 (dbBinary), Size=510, Attributes=2.
            return new NewField(col.Name, NewFieldType.VarBinary, Size: 510);
        }

        if (col.FieldType is not int daoType)
        {
            return FailMakeTableField(col, col.TypeOpen ?? "untyped expression", out failure);
        }

        return daoType switch
        {
            3 => new NewField(col.Name, NewFieldType.Short),
            4 => new NewField(col.Name, NewFieldType.Long, VariableLayout: true),
            5 => new NewField(col.Name, NewFieldType.Currency, VariableLayout: true),
            7 => new NewField(col.Name, NewFieldType.Double, VariableLayout: true),
            8 => new NewField(col.Name, NewFieldType.DateTime, VariableLayout: true),
            9 => new NewField(col.Name, NewFieldType.VarBinary, Size: col.Size is > 0 and <= 510 ? col.Size.Value : 510),
            10 => new NewField(col.Name, NewFieldType.Text, Size: col.Size is > 0 and <= 255 ? col.Size.Value : 255),
            16 => new NewField(col.Name, NewFieldType.BigInt),
            20 => new NewField(col.Name, NewFieldType.Decimal, Precision: col.DecimalPrecision ?? 28, Scale: col.DecimalScale ?? 0, VariableLayout: true),
            _ => FailMakeTableField(col, $"DAO FieldType {daoType}", out failure),
        };
    }

    private static NewField? FailMakeTableField(ResultColumn col, string detail, out SqlFailure? failure)
    {
        failure = SqlFailure.Open(SqlPhase.Bind, "NotModelled.MakeTableColumnType", $"SELECT ... INTO column '{col.Name}' ({detail}) is not modelled");
        return null;
    }

    private SqlFailure? BindInsertValues()
    {
        if (_syntax.TargetColumns is { } explicitCols)
        {
            if (ResolveTargetColumns(explicitCols) is { } colFailure)
            {
                return colFailure;
            }
            if (_insertColumns.Count != _syntax.Values.Count)
            {
                // r-err-1b/sql-insert-count-mismatch, r-def-expr-sql/sql-insert-values-forms: arity mismatch -> 3346.
                return SqlFailure.Ace(SqlPhase.Bind, 3346, SqlMessages.ValueFieldCountMismatch);
            }
        }
        else
        {
            // r-sql-2-3/act-ins-mapping, r-def-expr-sql/sql-insert-values-forms: omitted column list maps positionally.
            if (TargetTable.Columns.Count != _syntax.Values.Count)
            {
                return SqlFailure.Ace(SqlPhase.Bind, 3346, SqlMessages.ValueFieldCountMismatch);
            }
            _insertColumns.AddRange(TargetTable.Columns);
        }

        var scope = new BoundSelect.SourceScope([], [], ParameterList, Source);
        foreach (Fragment value in _syntax.Values)
        {
            if (BindExpression(value, scope, out BoundExpression? expr) is { } ef)
            {
                return ef;
            }
            _insertValues.Add(expr!);
            _insertValueFragments.Add(value);
        }
        return null;
    }

    private SqlFailure? BindInsertSelect(ISqlDatabase database)
    {
        SelectSyntax select = _syntax.SourceSelect!;
        if (_syntax.SourceSelectHasFrom)
        {
            // INSERT INTO T (c1, c2, ...) SELECT ... maps the SELECT's outputs by position rather than by name, so
            // repeated unqualified unaliased base columns of one source table (evidence/r-q27-autonumber-txn
            // ins-clean-two-pages, ins-dup-fail-two-pages: SELECT K, P, P, P, P) do not collide; other collisions stay refused.
            BoundSelect? boundSelect = BoundSelect.BindWithParameters(
                select,
                database,
                ParameterList,
                out SqlFailure? sf,
                allowRepeatedSingleSourceColumn: _syntax.TargetColumns is not null);
            if (boundSelect is null)
            {
                return sf;
            }
            SourceSelect = boundSelect;
            if (boundSelect.ParametersFailure is { } pf)
            {
                ParametersFailure ??= pf;
            }

            if (_syntax.TargetColumns is { } explicitCols)
            {
                if (ResolveTargetColumns(explicitCols) is { } colFailure)
                {
                    return colFailure;
                }
                if (_insertColumns.Count != boundSelect.Columns.Count)
                {
                    // r-sql-2-3/act-ins-mapping: mismatched SELECT column count -> 3346.
                    return SqlFailure.Ace(SqlPhase.Bind, 3346, SqlMessages.ValueFieldCountMismatch);
                }
            }
            else
            {
                // R-SQL-2 ACT-INS-03 (r-sql-2-3/act-ins-mapping, r-def-expr-sql/sql-insert-values-forms):
                // INSERT INTO T SELECT ... without a target column list maps each source output column by name;
                // an unknown name fails 3127.
                var names = new List<string>(boundSelect.Columns.Count);
                foreach (ResultColumn col in boundSelect.Columns)
                {
                    names.Add(col.Name);
                }
                if (ResolveTargetColumns(names) is { } mapFailure)
                {
                    return mapFailure;
                }
            }
            return null;
        }

        // FROM-less constant SELECT inside INSERT (r-def-expr-sql/sql-insert-values-forms: INSERT INTO U (ID, K) SELECT 602, 'y').
        var scope = new BoundSelect.SourceScope([], [], ParameterList, Source);
        var projectedNames = new List<string>(select.Items.Count);
        foreach (SelectItem item in select.Items)
        {
            if (item.Star || item.Expression is null)
            {
                return SqlFailure.Open(SqlPhase.Bind, "NotModelled.InsertConstantSelectStar", "SELECT * without FROM in INSERT INTO");
            }
            if (BindExpression(item.Expression, scope, out BoundExpression? expr) is { } ef)
            {
                return ef;
            }
            _insertValues.Add(expr!);
            _insertValueFragments.Add(item.Expression);
            projectedNames.Add(item.Alias ?? "");
        }
        if (_syntax.TargetColumns is { } explicitConstCols)
        {
            if (ResolveTargetColumns(explicitConstCols) is { } colFailure)
            {
                return colFailure;
            }
            if (_insertColumns.Count != _insertValues.Count)
            {
                return SqlFailure.Ace(SqlPhase.Bind, 3346, SqlMessages.ValueFieldCountMismatch);
            }
        }
        else
        {
            if (projectedNames.Any(string.IsNullOrEmpty))
            {
                return SqlFailure.Open(SqlPhase.Bind, "NotModelled.InsertConstantSelectMapping", "FROM-less INSERT INTO SELECT without target column list or aliases");
            }
            if (ResolveTargetColumns(projectedNames) is { } mapFailure)
            {
                return mapFailure;
            }
        }
        return null;
    }

    private SqlFailure? BindUpdate()
    {
        string scopeName = _syntax.TargetAlias ?? _syntax.TargetName;
        var slot = new SourceSlot(0, scopeName, _syntax.TargetName, TargetTable);
        var scope = new BoundSelect.SourceScope([slot], [], ParameterList, Source);
        var seen = new HashSet<int>();

        foreach (SetAssignment a in _syntax.Assignments)
        {
            if (a.Qualifier is { } qual
                && !string.Equals(qual, scopeName, StringComparison.OrdinalIgnoreCase)
                && !(_syntax.TargetAlias is null && string.Equals(qual, _syntax.TargetName, StringComparison.OrdinalIgnoreCase)))
            {
                return SqlFailure.Open(SqlPhase.Bind, "NotModelled.QualifiedSetTarget", $"SET target qualifier '{qual}' does not match '{scopeName}'");
            }
            ColumnDescriptor? col = slot.Column(a.Column);
            if (col is null)
            {
                return SqlFailure.Open(SqlPhase.Bind, "NotModelled.UnknownUpdateColumn", $"UPDATE SET target column '{a.Column}' is not a column of '{_syntax.TargetName}'");
            }
            if (!seen.Add(col.Ordinal))
            {
                // r-sql-2-3/act-upd-assign: UPDATE T SET V = 1, V = 2 -> 3063 Duplicate output destination 'V'.
                return SqlFailure.Ace(SqlPhase.Bind, 3063, SqlMessages.DuplicateDestination(a.Column));
            }
            if (BindExpression(a.Value, scope, out BoundExpression? rhs) is { } rf)
            {
                return rf;
            }
            _assignments.Add(new BoundAssignment(col, rhs!, a.Value));
        }

        if (_syntax.Where is { } where)
        {
            scope.InWhere = true;
            if (BindExpression(where, scope, out BoundExpression? boundWhere) is { } wf)
            {
                return wf;
            }
            Where = boundWhere;
        }
        return null;
    }

    private SqlFailure? BindDelete()
    {
        if (_syntax.Where is { } where)
        {
            string scopeName = _syntax.TargetAlias ?? _syntax.TargetName;
            var slot = new SourceSlot(0, scopeName, _syntax.TargetName, TargetTable);
            var scope = new BoundSelect.SourceScope([slot], [], ParameterList, Source) { InWhere = true };
            if (BindExpression(where, scope, out BoundExpression? boundWhere) is { } wf)
            {
                return wf;
            }
            Where = boundWhere;
        }
        return null;
    }

    private SqlFailure? ResolveTargetColumns(IReadOnlyList<string> names)
    {
        var seen = new HashSet<int>();
        foreach (string colName in names)
        {
            ColumnDescriptor? found = null;
            foreach (ColumnDescriptor c in TargetTable.Columns)
            {
                if (string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase))
                {
                    found = c;
                    break;
                }
            }
            if (found is null)
            {
                // r-def-expr-sql/sql-insert-values-forms: unknown destination field -> 3127.
                return SqlFailure.Ace(SqlPhase.Bind, 3127, SqlMessages.UnknownInsertField(colName));
            }
            if (!seen.Add(found.Ordinal))
            {
                // r-def-expr-sql/sql-insert-values-forms: INSERT INTO U (ID, ID) -> 3063.
                return SqlFailure.Ace(SqlPhase.Bind, 3063, SqlMessages.DuplicateDestination(colName));
            }
            _insertColumns.Add(found);
        }
        return null;
    }

    private SqlFailure? BindExpression(Fragment fragment, BoundSelect.SourceScope scope, out BoundExpression? bound)
    {
        bound = BoundExpression.Bind(fragment.Syntax.Root!, scope, Context, out ExpressionFailure? f);
        if (f is not null)
        {
            bound?.Dispose();
            bound = null;
            return SqlFailure.Of(f, SqlPhase.Bind, Source, fragment.Start, fragment.End);
        }
        _ownedExpressions.Add(bound!);
        return null;
    }

    public void Dispose()
    {
        foreach (BoundExpression e in _ownedExpressions)
        {
            e.Dispose();
        }
        _ownedExpressions.Clear();
        SourceSelect?.Dispose();
        SourceSelect = null;
        _syntax.Dispose();
    }
}
