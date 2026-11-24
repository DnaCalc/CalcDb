using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Format;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>
/// A saved QueryDef of a Database (II-06 §§8–11; R-CAT-1 CAT-Q, qualified), read from its catalog row and MSysQueries
/// role rows. Name, Type (the masked flag class), SQL (rendered as ACE renders it at open, <see cref="QuerySql"/>),
/// Connect (the pass-through type row's Name1, else ""), ReturnsRecords (True for every saved family in
/// r-cat-1/querydefs), Updatable (the session's: True on a read-write open, False read-only; r-fix-cursor-dao/updatable-members-rw),
/// the dates, the stored ODBCTimeout and MaxRecords, Parameters and Properties. A select query's Parameters, their
/// values, its Fields before an open and its OpenRecordset come from its SQL bound by the binder (W-06/parser-binder-scan;
/// qualified.md R-DAO-1 DAO-QUERY, R-SQL-3 SQL-META parameters). Close of a saved QueryDef changes nothing (DAO-LIFE:
/// its recordsets survive and SQL still reads).
/// </summary>
internal sealed unsafe class QueryDefObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.QueryDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, q => Text(q._query.Name)))
        .Get("Type", (self, args) => Live(self, q => q._query.Family is { } f ? Int16((int)f) : CallResult.Fail(Failure.NotModelled("NotModelled.QueryFamily", $"QueryDef.Type of flag class 0x{q._query.FlagClass:X2}"))))
        .Get("SQL", (self, args) => Live(self, q => q.Sql()))
        .Get("Connect", (self, args) => Live(self, q => Text(q._query.Family == QueryFamily.PassThrough ? q._query.TypeRow?.Name1 ?? "" : "")))
        .Get("ReturnsRecords", (self, args) => Live(self, q => Bool(true)))
        .Get("Updatable", (self, args) => Live(self, q => Bool(!q.Database.ReadOnly)))
        .Get("DateCreated", (self, args) => Live(self, q => CatalogDates.Date(q._query.Entry.DateCreateBits)))
        .Get("LastUpdated", (self, args) => Live(self, q => CatalogDates.Date(q._query.Entry.DateUpdateBits)))
        .Get("ODBCTimeout", (self, args) => Live(self, q => q.StoredOr("ODBCTimeout", Int16(60))))
        .Get("MaxRecords", (self, args) => Live(self, q => q.StoredOr("MaxRecords", Int32(0))))
        .Get("RecordsAffected", (self, args) => Live(self, q => Int32(q._recordsAffected)))
        .Get("StillExecuting", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("CacheSize", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Prepare", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Parameters", (self, args) => Live(self, q => Object(q.ParametersCollection())))
        .Get("Fields", (self, args) => Live(self, q => q.FieldsCollection()))
        .Get("Properties", (self, args) => Live(self, q => q.PropertiesCollection()))
        .Method("OpenRecordset", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, options = args.Length > 1 ? args[1] : 0; return Live(self, q => q.OpenRecordset(type, options)); })
        .Method("Execute", (self, args) => { nint options = args.Length > 0 ? args[0] : 0; return Live(self, q => q.Execute(options)); })
        .Method("Close", (self, args) => Live(self, q => CallResult.Nothing));

    private readonly DefinitionCollection _owner;
    private readonly SavedQuery _query;
    private readonly PropertyMap? _stored;
    private DefinitionCollection? _parameters;
    private PropertiesObject? _properties;
    private BoundSelect? _plan;
    private BoundAction? _action;
    private int _recordsAffected;
    private SqlFailure? _planFailure;
    private bool _planMade;
    private NativeVariant?[] _values = [];

    public QueryDefObject(DefinitionCollection owner, SavedQuery query) : base(Decl, Vtable, owner.Engine, "DAO.QueryDef")
    {
        _owner = owner;
        _query = query;
        owner.AddRef();
        _stored = owner.Database.StoredProperties(query.Entry, out _storedFailure);
    }

    private readonly string? _storedFailure;

    private DatabaseObject Database => _owner.Database;

    public override bool Invalid => Database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<QueryDefObject, CallResult> op)
    {
        var q = (QueryDefObject)self;
        return q.Invalid ? q.Fail(3420, DaoErrors.ObjectInvalid) : op(q);
    }

    private CallResult Sql()
    {
        RenderedSql sql = QuerySql.Render(_query);
        return sql.Text is { } text ? Text(text) : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"QueryDef.SQL of '{_query.Name}': {sql.NotModelled}"));
    }

    /// <summary>
    /// OpenRecordset(Type, Options): the saved query's recordset, as Database.OpenRecordset opens it by name, with this
    /// QueryDef's parameter values (DAO-QUERY: without values 3061; d1e/query-parameter-values: pId = "2" opens the row 2).
    /// The cursor keeps copies of the values, so a later assignment does not reach it (EXPR-BIND).
    /// </summary>
    private CallResult OpenRecordset(nint typeArg, nint optionsArg)
    {
        int? type = Args.Int32(typeArg, omitted: null);
        int? options = Args.Int32(optionsArg, omitted: 0);
        if (options is null || (typeArg != 0 && type is null))
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.OpenRecordset with a Type or Options argument that is not numeric"));
        }
        CallResult result = Plan() is not null && QuerySql.Render(_query).Text is { } text
            ? SelectRoute.Open(Database, text, type, options.Value, _values)
            : Database.OpenSaved(_query, type, options.Value);
        Opened |= result.Failure is null;
        return result;
    }

    /// <summary>Whether a recordset of this QueryDef was opened: QueryDef.Fields is recorded before an open only (SQL-META).</summary>
    public bool Opened { get; private set; }

    /// <summary>
    /// The select query's SQL as ACE renders it (QuerySql), bound once (W-06/parser-binder-scan); null for the other
    /// families, and when the SQL does not render or bind (<see cref="_planFailure"/> holds the binder's failure). A
    /// delete, update or append query's SQL is bound once too, as its action plan (<see cref="_action"/>, W-07/actions),
    /// whose parameters its Parameters list and Execute takes (r-cat-1b/qd-execute-param-action).
    /// </summary>
    public BoundSelect? Plan()
    {
        if (!_planMade)
        {
            _planMade = true;
            if (_query.Family is QueryFamily.Select or QueryFamily.Delete or QueryFamily.Update or QueryFamily.Append && QuerySql.Render(_query).Text is { } text)
            {
                if (_query.Family == QueryFamily.Select)
                {
                    _plan = BoundSelect.Prepare(text, Database.Sql, out _planFailure);
                }
                else
                {
                    _action = BoundAction.Prepare(text, Database.Sql, out _planFailure);
                }
                _values = BoundParameters is { } bound ? new NativeVariant?[bound.Count] : [];
            }
        }
        return _plan;
    }

    /// <summary>The bound statement's parameters (a select's or an action's), or null when neither bound.</summary>
    private IReadOnlyList<Sql.QueryParameter>? BoundParameters => _plan?.Parameters ?? _action?.Parameters;

    /// <summary>Parameter <paramref name="index"/> of the bound statement.</summary>
    public Sql.QueryParameter Parameter(int index)
    {
        Plan();
        return BoundParameters![index];
    }

    /// <summary>The value assigned to parameter <paramref name="index"/>, or null (DAO-QUERY: an untouched Parameter reads Empty).</summary>
    public NativeVariant? ParameterValue(int index) => _values[index];

    /// <summary>Keep an adapted value (owned by this QueryDef), freeing the one it replaces.</summary>
    public void SetParameterValue(int index, NativeVariant adapted)
    {
        if (_values[index] is { } old)
        {
            SqlParameters.Release(ref old);
        }
        _values[index] = adapted;
    }

    /// <summary>
    /// QueryDef.Fields of a select query before an open: the bound columns (qualified.md SQL-META: readable before any
    /// value is assigned). The other families, an unbound SQL and Fields after an open are named.
    /// </summary>
    private CallResult FieldsCollection()
    {
        if (Plan() is null)
        {
            return CallResult.Fail(Failure.NotImplemented(_planFailure is { } f
                ? $"QueryDef.Fields of '{_query.Name}' whose SQL does not bind here: {f.NotModelled ?? f.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f.Message}"
                : $"QueryDef.Fields of a {_query.Family?.ToString() ?? "unclassified"} query (the binder reads SELECT; W-06, W-07)"));
        }
        var fields = new QueryFieldsObject(this);
        CallResult result = Object(fields);
        fields.Release();
        return result;
    }

    /// <summary>
    /// Execute(Options): a row-returning query (select, crosstab, union) is 3065 from DAO.QueryDef, before any parameter is
    /// asked for (r-cat-1b/qd-execute-on-row-returning: QSel, QXtab, QUni, QParam). A delete, update or append query runs
    /// its bound plan with this QueryDef's parameter values (missing ones: 3061 from DAO.QueryDef,
    /// r-cat-1b/qd-execute-param-action); the other families run their rendered SQL (<see cref="ActionRoute.ExecuteSaved"/>).
    /// RecordsAffected counts on the QueryDef only (r-cat-1b/qd-execute-action-saved: QUpd 4, QApp 4, db 0 after each).
    /// An Options value that does not convert is not recorded on a QueryDef and is named.
    /// </summary>
    private CallResult Execute(nint optionsArg)
    {
        if (_query.Family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
        {
            _recordsAffected = 0;
            return Fail(3065, SelectRoute.CannotExecuteSelect);
        }
        if (Args.Int32(optionsArg, omitted: 0) is not { } options)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.Execute with an Options argument that is not numeric (not recorded)"));
        }
        Plan();
        if (_action is { } action)
        {
            return ActionRoute.Execute(Database, this, action, options, _values, ref _recordsAffected);
        }
        if (_planFailure is { } failure)
        {
            return ActionRoute.Failed(Database, this, failure, ref _recordsAffected);
        }
        return ActionRoute.ExecuteSaved(Database, this, _query, options, null, ref _recordsAffected);
    }

    /// <summary>A built-in the blob stores (flag 01: ODBCTimeout, MaxRecords), else the value ACE shows for an unset one.</summary>
    private CallResult StoredOr(string name, CallResult unset)
    {
        if (_storedFailure is { } failure)
        {
            return CallResult.Fail(Failure.NotImplemented($"QueryDef.{name} when the property blob cannot be read: {failure}"));
        }
        StoredProperty? stored = _stored?.Properties.FirstOrDefault(p => p.Name == name);
        return stored is null ? unset : PropertyObject.StoredValue(stored);
    }

    /// <summary>
    /// Parameters (qualified.md R-DAO-1 DAO-QUERY, R-SQL-3 SQL-META parameters): a select query's are its bound statement's
    /// (<see cref="BoundSelect.Parameters"/>: the declared ones in declaration order, then the implicit ones in order of
    /// first use; r-cat-1/querydefs QImplicit: pMin, Type 10); Item finds a name without the brackets it was declared
    /// with. One name declared with two types fails Count with 3060 from DAO.Parameters (r-sql-1/b-parameters); Item is
    /// then not recorded. A pass-through query has none (QPass: Count 0). The other families' parameters need a binder
    /// of their statements (W-07) and are named.
    /// </summary>
    private DefinitionCollection ParametersCollection()
    {
        if (_parameters is not null)
        {
            return _parameters;
        }
        if (_query.Family == QueryFamily.PassThrough)
        {
            return _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [], (c, i) => throw new InvalidOperationException("a pass-through query has no parameters"))
            {
                ClosedSource = "DAO.QueryDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
            };
        }
        Plan();
        if (BoundParameters is not { } parameters)
        {
            return _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [], (c, i) => throw new InvalidOperationException("no parameters"))
            {
                ClosedSource = "DAO.QueryDef",
                Gap = _planFailure is { } f
                    ? $"the Parameters of '{_query.Name}' whose SQL does not bind here: {f.NotModelled ?? f.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f.Message}"
                    : $"the Parameters of a {_query.Family?.ToString() ?? "unclassified"} query (the binder reads SELECT; W-07/actions)",
            };
        }
        SqlFailure? declarations = _plan?.ParametersFailure ?? _action?.ParametersFailure;
        _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [.. parameters.Select(p => Unbracketed(p.Name))],
            (c, i) => new ParameterObject(c, this, i))
        {
            ClosedSource = "DAO.QueryDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
            Gap = declarations is { IsNotModelled: true } open ? $"{open.NotModelled}: {open.Message}" : null,
            CountDenied = declarations is { IsNotModelled: false } ace ? (ace.Number, ace.Message) : null,
            ItemGap = declarations is { IsNotModelled: false } ? "Parameters.Item of a statement declaring one name with two types (Count fails 3060; Item is not recorded)" : null,
        };
        return _parameters;
    }

    /// <summary>The name a parameter is looked up by: without the brackets it was declared with (the binder's QueryParameter key).</summary>
    private static string Unbracketed(string name) => name is ['[', .., ']'] ? name[1..^1] : name;

    /// <summary>
    /// The 14 built-ins, then the stored ones (r-cat-1/querydefs QProp: Description, Text, after Prepare). A row-returning
    /// single-table SELECT also lists its table's properties (R-PRESERVE-1 PRES-QUERY-PROPS); where that table stores
    /// any, the inherited entries are not modelled.
    /// </summary>
    private CallResult PropertiesCollection()
    {
        if (_storedFailure is { } failure)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.Properties when the property blob cannot be read: " + failure));
        }
        if (Database.InheritsTableProperties(_query))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.InheritedQueryProperties", $"the table properties '{_query.Name}' inherits (PRES-QUERY-PROPS)"));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.QueryDef, _stored));
        return Object(_properties);
    }

    protected override void OnFinalRelease()
    {
        _parameters?.Release();
        _properties?.Release();
        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i] is { } value)
            {
                SqlParameters.Release(ref value);
                _values[i] = null;
            }
        }
        _plan?.Dispose();
        _action?.Dispose();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Parameter of a select QueryDef (qualified.md R-DAO-1 DAO-QUERY; d1e/query-parameter-values): Name as declared, Type
/// its DAO type, Direction 1 (dbParamInput) for a declared one, and Value: VT_EMPTY until assigned, then the value as
/// <see cref="SqlParameters.Adapt"/> converts it to the declared type (Empty reads VT_I4 0 for a Long, Null VT_NULL, "2"
/// VT_I4 2). A value the type refuses is 3421 "Data type conversion error." from DAO.Parameter without an IErrorInfo and
/// keeps the old value; a value Adapt does not model is named.
/// </summary>
internal sealed unsafe class ParameterObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Parameter;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, p => Text(p.Parameter.Name)))
        .Get("Type", (self, args) => Live(self, p => Int16(p.Parameter.DaoType)))
        .Get("Direction", (self, args) => Live(self, p => p.Parameter.Declared ? Int16(1) : CallResult.Fail(Failure.NotModelled("NotModelled.ParameterDirection", "Direction of an implicit parameter (not recorded)"))))
        .Get("Value", (self, args) => Live(self, p => p._query.ParameterValue(p._index) is { } value ? SqlResults.Of(value, "Parameter.Value") : CallResult.OfScalar(VarType.Empty, 0)))
        .Put("Value", (self, args) => { nint value = args[^1]; return Live(self, p => p.Assign(value)); });

    private readonly DefinitionCollection _owner;
    private readonly QueryDefObject _query;
    private readonly int _index;

    public ParameterObject(DefinitionCollection owner, QueryDefObject query, int index) : base(Decl, Vtable, owner.Engine, "DAO.Parameter")
    {
        _owner = owner;
        _query = query;
        _index = index;
        owner.AddRef();
        query.AddRef(); // its value lives on the QueryDef (CAT-V: one instance's values are shared by its Parameters)
    }

    private Sql.QueryParameter Parameter => _query.Parameter(_index);

    private CallResult Assign(nint value)
    {
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        SqlFailure? refused = SqlParameters.Adapt(Parameter, incoming, _owner.Database.Sql.Context, out NativeVariant adapted);
        if (refused is not null)
        {
            return refused.IsNotModelled ? CallResult.Fail(Failure.NotModelled(refused.NotModelled!, refused.Message)) : Fail(refused.Number, refused.Message);
        }
        _query.SetParameterValue(_index, adapted);
        return CallResult.Nothing;
    }

    public override bool Invalid => _owner.Database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<ParameterObject, CallResult> op)
    {
        var p = (ParameterObject)self;
        return p.Invalid ? p.Fail(3420, DaoErrors.ObjectInvalid) : op(p);
    }

    protected override void OnFinalRelease()
    {
        _owner.Released(this);
        _owner.Release();
        _query.Release();
    }
}

/// <summary>
/// QueryDef.Fields of a select query before an open (qualified.md R-SQL-3 SQL-META: readable before assignment): the
/// bound columns in select-list order, Item by name or ordinal (3265 otherwise). After an open of the QueryDef its members
/// are named: the records read them before one.
/// </summary>
internal sealed unsafe class QueryFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, f => Int16(f.Columns.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, f => f.Item(index)); })
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private CollectionNames? _names;

    /// <summary>A collection per Fields get (its identity is not recorded); it holds its QueryDef, whose plan its columns are.</summary>
    public QueryFieldsObject(QueryDefObject query) : base(Decl, Vtable, query.Engine, "DAO.Fields")
    {
        Query = query;
        query.AddRef();
    }

    protected override void OnFinalRelease() => Query.Release();

    public QueryDefObject Query { get; }

    public IReadOnlyList<ResultColumn> Columns => Query.Plan()!.Columns;

    public override bool Invalid => Query.Invalid;

    protected override OperationTable Table => Operations;

    public static CallResult Live<T>(T self, Func<T, CallResult> op, QueryDefObject query) where T : DaoObject =>
        query.Invalid ? self.Fail(3420, DaoErrors.ObjectInvalid)
        : query.Opened ? CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefFieldsAfterOpen", "QueryDef.Fields after an OpenRecordset of the QueryDef (SQL-META records it before one)"))
        : op(self);

    private static CallResult Live(ComObject self, Func<QueryFieldsObject, CallResult> op)
    {
        var f = (QueryFieldsObject)self;
        return Live(f, op, f.Query);
    }

    private CallResult Item(nint index)
    {
        _names ??= new CollectionNames([.. Columns.Select(c => c.Name)]);
        int? ordinal = CollectionLookup.Find(index, _names, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.Fields.Item with an object index"));
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        var field = new QueryFieldObject(this, i);
        CallResult result = Object(field);
        field.Release();
        return result;
    }
}

/// <summary>
/// A column of a select QueryDef before an open: Name, and Type where the binder fixes it (<see cref="ColumnTypeRule.Bound"/>);
/// a type known only at open or not identified is named with the binder's reason.
/// </summary>
internal sealed unsafe class QueryFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.Column is { TypeRule: ColumnTypeRule.Bound, FieldType: int type }
            ? Int16(type)
            : CallResult.Fail(Failure.NotModelled(f.Column.TypeOpen ?? "NotModelled.ExpressionFieldType", $"QueryDef Field.Type of the column {f.Column.Name} before an open"))));

    private readonly QueryFieldsObject _fields;
    private readonly int _ordinal;

    public QueryFieldObject(QueryFieldsObject fields, int ordinal) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        fields.AddRef();
    }

    private ResultColumn Column => _fields.Columns[_ordinal];

    public override bool Invalid => _fields.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<QueryFieldObject, CallResult> op)
    {
        var f = (QueryFieldObject)self;
        return QueryFieldsObject.Live(f, op, f._fields.Query);
    }

    protected override void OnFinalRelease() => _fields.Release();
}

/// <summary>The catalog DateCreate and DateUpdate bits as VT_DATE, exactly (TableDef, QueryDef, Document dates).</summary>
internal static class CatalogDates
{
    public static CallResult Date(ulong? bits) => bits is ulong b
        ? CallResult.OfScalar(Foundation.VarType.Date, (long)b)
        : CallResult.Fail(Failure.NotImplemented("a catalog date of a row without that field"));
}
