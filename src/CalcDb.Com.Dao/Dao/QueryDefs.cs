using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Format;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

internal enum QueryDefMode
{
    Saved,
    Draft,
    Temporary,
}

/// <summary>
/// A DAO QueryDef (II-06 §§8–11; R-CAT-1 CAT-Q, R-DAO-1 DAO-QUERY, W-07/saved-definition): saved (read from its
/// catalog row and MSysQueries role rows, or created via <c>Database.CreateQueryDef(name, sql)</c> /
/// <c>QueryDefs.Append</c>), draft (<c>CreateQueryDef()</c> before <c>Append</c>), or temporary
/// (<c>CreateQueryDef("", sql)</c>, <c>#Temporary QueryDef#</c>, writing no bytes). One class serves all three states
/// so <c>QueryDefs.Append(draft)</c> transitions the same COM instance in place (<c>r-cat-1/temporary-querydef</c>,
/// <c>r-dao-1-err-1/draft-querydef</c>).
/// </summary>
internal sealed unsafe class QueryDefObject : DaoObject
{
    public const string TemporaryName = "#Temporary QueryDef#";

    private static readonly InterfaceDecl Decl = DaoTypes.QueryDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, q => Text(q.CurrentName)))
        .Put("Name", (self, args) => { nint value = args[^1]; return Live(self, q => q.PutName(value)); })
        .Get("Type", (self, args) => Live(self, q => q.ReadType()))
        .Get("SQL", (self, args) => Live(self, q => q.Sql()))
        .Put("SQL", (self, args) => { nint value = args[^1]; return Live(self, q => q.PutSql(value)); })
        .Get("Connect", (self, args) => Live(self, q => Text(q.CurrentConnect)))
        .Put("Connect", (self, args) => { nint value = args[^1]; return Live(self, q => q.PutConnect(value)); })
        .Get("ReturnsRecords", (self, args) => Live(self, q => Bool(true)))
        .Put("ReturnsRecords", (self, args) => { nint value = args[^1]; return Live(self, q => q.PutReturnsRecords(value)); })
        .Get("Updatable", (self, args) => Live(self, q => Bool(!q.Database.ReadOnly)))
        .Get("DateCreated", (self, args) => Live(self, q => q._query is { } s ? CatalogDates.Date(s.Entry.DateCreateBits) : CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefDraftDate", "DateCreated on an unsaved QueryDef"))))
        .Get("LastUpdated", (self, args) => Live(self, q => q._query is { } s ? CatalogDates.Date(s.Entry.DateUpdateBits) : CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefDraftDate", "LastUpdated on an unsaved QueryDef"))))
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

    private readonly DatabaseObject _database;
    private DefinitionCollection? _owner;
    private QueryDefMode _mode;
    private SavedQuery? _query;
    private PropertyMap? _stored;
    private string? _storedFailure;
    private string _draftName = "";
    private string _rawSql = "";
    private string _connect = "";
    private bool _deleted;
    private int? _sessionTypeOverride;
    private string? _savedSqlOverride;
    private DefinitionCollection? _parameters;
    private PropertiesObject? _properties;
    private BoundSelect? _plan;
    private BoundAction? _action;
    private int _recordsAffected;
    private SqlFailure? _planFailure;
    private bool _planMade;
    private NativeVariant?[] _values = [];
    private bool[] _parameterTypeMismatch = [];
    private int _sqlGeneration;

    /// <summary>Wrap an already-saved QueryDef owned by <paramref name="owner"/> (or created in this session).</summary>
    public QueryDefObject(
        DefinitionCollection? owner,
        DatabaseObject database,
        SavedQuery query,
        bool createdInSession = false,
        int? sessionTypeOverride = null,
        string? savedSqlOverride = null)
        : base(Decl, Vtable, database.Engine, "DAO.QueryDef")
    {
        _database = database;
        _owner = owner;
        _mode = QueryDefMode.Saved;
        _query = query;
        _sessionTypeOverride = sessionTypeOverride ?? (createdInSession ? 0 : null);
        _savedSqlOverride = savedSqlOverride;
        if (owner is not null)
        {
            owner.AddRef();
        }
        else
        {
            database.AddRef();
        }
        _stored = database.StoredProperties(query.Entry, out _storedFailure);
    }

    public QueryDefObject(DefinitionCollection owner, SavedQuery query)
        : this(owner, owner.Database, query, createdInSession: false)
    {
    }

    /// <summary>Create an unsaved Draft (<c>CreateQueryDef()</c>) or Temporary (<c>CreateQueryDef("", sql)</c>) QueryDef.</summary>
    public QueryDefObject(DatabaseObject database, QueryDefMode mode, string sql)
        : base(Decl, Vtable, database.Engine, "DAO.QueryDef")
    {
        if (mode == QueryDefMode.Saved)
        {
            throw new ArgumentException("Use the SavedQuery constructor for saved QueryDefs.", nameof(mode));
        }
        _database = database;
        _mode = mode;
        _rawSql = sql;
        database.AddRef();
    }

    public QueryDefMode Mode => _mode;

    public int? ObjectId => _query?.Entry.Id;

    public int? SessionTypeOverride => _sessionTypeOverride;

    internal int SqlGeneration => _sqlGeneration;

    public string DraftName => _draftName;

    public string DraftSql => _rawSql;

    public string DraftConnect => _connect;

    private string CurrentName => _mode switch
    {
        QueryDefMode.Saved => _query!.Name,
        QueryDefMode.Draft => _draftName,
        QueryDefMode.Temporary => TemporaryName,
        _ => "",
    };

    private string CurrentConnect => _mode == QueryDefMode.Saved
        ? (_query!.Family == QueryFamily.PassThrough ? _query.TypeRow?.Name1 ?? "" : "")
        : _connect;

    private QueryFamily? EffectiveFamily => _mode == QueryDefMode.Saved
        ? _query!.Family
        : QueryDefinitionCompiler.ClassifyFamily(_rawSql, _connect);

    private DatabaseObject Database => _database;

    public override bool Invalid => _deleted || Database.Closed;

    protected override OperationTable Table => Operations;

    /// <summary>
    /// Mark this saved QueryDef as deleted by <c>QueryDefs.Delete</c>: every subsequent member access on this
    /// wrapper and on its held <c>Parameters</c>, <c>Parameter</c>, <c>Fields</c>, <c>Field</c>, and <c>Properties</c>
    /// fails 3420 (<c>r-cat-1/held-querydef-lifetime</c>, <c>r-def-cursor-dao/err-held-querydef-after-delete</c>).
    /// </summary>
    public void MarkDeleted() => _deleted = true;

    /// <summary>
    /// Transition a Draft QueryDef to Saved in place when <c>QueryDefs.Append(draft)</c> publishes it, preserving
    /// COM identity (<c>r-cat-1/temporary-querydef</c> [55]-[58], <c>r-q39-querydef-bytes/draft-append-accdb</c>).
    /// </summary>
    public void TransitionToSaved(DefinitionCollection? owner, SavedQuery saved)
    {
        _mode = QueryDefMode.Saved;
        _query = saved;
        _sessionTypeOverride = 0;
        _savedSqlOverride = null;
        _stored = _database.StoredProperties(saved.Entry, out _storedFailure);
        _properties?.Release();
        _properties = null;
        ResetPlan();
        if (owner is not null && _owner is null)
        {
            owner.AddRef();
            _owner = owner;
            _database.Release();
        }
    }

    /// <summary>
    /// Apply a published <c>QueryDef.SQL</c> put to this live saved wrapper: keeps pre-change session <c>Type</c>
    /// until reopen (<c>r-cat-1b/qd-sql-change-type-tracking</c>, <c>r-q39/sql-put-type-change-accdb</c>), keeps the
    /// verbatim SQL until reopen (<c>r-cat-1/held-sql-replace</c>, <c>r-dao-1-err-1/query-sql-rewrite</c>), reloads
    /// the catalog row (<c>LastUpdated</c>) and <c>MSysQueries</c> plan, and rebinds held <c>Parameters</c> in place
    /// so the collection keeps its COM identity, surviving held <c>Parameter</c> objects keep their identity, old
    /// <c>Type</c>, and old <c>Value</c>, and removed <c>Parameter</c> objects become permanently invalid (3420).
    /// </summary>
    public void ApplySavedSqlUpdate(SavedQuery updated, string verbatimSql)
    {
        _sessionTypeOverride ??= _query?.Family is { } oldFamily ? (int)oldFamily : null;
        _query = updated;
        _savedSqlOverride = verbatimSql;
        _stored = _database.StoredProperties(updated.Entry, out _storedFailure);
        _properties?.Release();
        _properties = null;
        Opened = false;
        _sqlGeneration++;

        IReadOnlyList<(string Name, DaoObject Wrapper)> priorHeld = _parameters?.SnapshotHeld() ?? [];
        List<(string Name, ParameterObject Param)> priorParams = new(priorHeld.Count);
        Dictionary<int, int> heldDaoTypeByBoundIndex = [];
        foreach ((string name, DaoObject wrapper) in priorHeld)
        {
            if (wrapper is ParameterObject param)
            {
                priorParams.Add((name, param));
                heldDaoTypeByBoundIndex.TryAdd(param.BoundIndex, param.ParameterInfo.DaoType);
            }
        }

        Dictionary<string, (NativeVariant Value, int DaoType)> priorValuesByName = new(StringComparer.OrdinalIgnoreCase);
        if (BoundParameters is { } oldBound)
        {
            int count = Math.Min(oldBound.Count, _values.Length);
            for (int i = 0; i < count; i++)
            {
                if (_values[i] is { } existingValue)
                {
                    int daoType = heldDaoTypeByBoundIndex.TryGetValue(i, out int heldType) ? heldType : oldBound[i].DaoType;
                    priorValuesByName[Unbracketed(oldBound[i].Name)] = (existingValue, daoType);
                    _values[i] = null;
                }
            }
        }

        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i] is { } leftover)
            {
                SqlParameters.Release(ref leftover);
                _values[i] = null;
            }
        }
        _values = [];
        _parameterTypeMismatch = [];
        _plan?.Dispose();
        _plan = null;
        _action?.Dispose();
        _action = null;
        _planFailure = null;
        _planMade = false;

        if (_parameters is null)
        {
            ReleasePriorValues(priorValuesByName);
            return;
        }

        QueryFamily? family = EffectiveFamily;
        if (family is QueryFamily.PassThrough or QueryFamily.DataDefinition)
        {
            InvalidatePriorParameters(priorParams, priorValuesByName);
            _parameters.Rebind([], static (_, _) => throw new InvalidOperationException("no parameters"));
            return;
        }

        Plan();
        if (BoundParameters is not { } newBound)
        {
            InvalidatePriorParameters(priorParams, priorValuesByName);
            string gap = _planFailure is { } f
                ? $"the Parameters of '{CurrentName}' whose SQL does not bind here: {f.NotModelled ?? f.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f.Message}"
                : $"the Parameters of a {family?.ToString() ?? "unclassified"} query (the binder reads SELECT; W-07/actions)";
            _parameters.Rebind([], static (_, _) => throw new InvalidOperationException("no parameters"), gap: gap);
            return;
        }

        Dictionary<string, int> newByName = new(newBound.Count, StringComparer.OrdinalIgnoreCase);
        for (int b = 0; b < newBound.Count; b++)
        {
            newByName.TryAdd(Unbracketed(newBound[b].Name), b);
        }

        List<string> newNames = new(newBound.Count);
        List<Sql.QueryParameter> effectiveParams = new(newBound.Count);
        List<int> boundIndices = new(newBound.Count);
        Dictionary<int, DaoObject> preserved = [];
        HashSet<int> usedNewBound = [];

        foreach ((string oldName, ParameterObject param) in priorParams)
        {
            if (newByName.TryGetValue(oldName, out int newBoundIndex) && usedNewBound.Add(newBoundIndex))
            {
                int collectionOrdinal = newNames.Count;
                newNames.Add(oldName);
                effectiveParams.Add(param.ParameterInfo);
                boundIndices.Add(newBoundIndex);
                param.Rebind(newBoundIndex);
                preserved[collectionOrdinal] = param;
                if (priorValuesByName.Remove(oldName, out (NativeVariant Value, int DaoType) carried))
                {
                    _values[newBoundIndex] = carried.Value;
                }
                if (param.ParameterInfo.DaoType != newBound[newBoundIndex].DaoType)
                {
                    _parameterTypeMismatch[newBoundIndex] = true;
                }
            }
            else
            {
                param.Invalidate();
            }
        }

        for (int b = 0; b < newBound.Count; b++)
        {
            if (usedNewBound.Add(b))
            {
                string paramName = Unbracketed(newBound[b].Name);
                newNames.Add(paramName);
                effectiveParams.Add(newBound[b]);
                boundIndices.Add(b);
                if (priorValuesByName.Remove(paramName, out (NativeVariant Value, int DaoType) carried))
                {
                    _values[b] = carried.Value;
                    if (carried.DaoType != newBound[b].DaoType)
                    {
                        _parameterTypeMismatch[b] = true;
                    }
                }
            }
        }

        ReleasePriorValues(priorValuesByName);

        SqlFailure? declarations = _plan?.ParametersFailure ?? _action?.ParametersFailure;
        _parameters.Rebind(
            newNames,
            (c, i) => new ParameterObject(c, this, effectiveParams[i], boundIndices[i]),
            preservedHeld: preserved,
            gap: declarations is { IsNotModelled: true } open ? $"{open.NotModelled}: {open.Message}" : null,
            countDenied: declarations is { IsNotModelled: false } ace ? (ace.Number, ace.Message) : null,
            itemGap: declarations is { IsNotModelled: false } ? "Parameters.Item of a statement declaring one name with two types (Count fails 3060; Item is not recorded)" : null);
    }

    private static void InvalidatePriorParameters(
        List<(string Name, ParameterObject Param)> priorParams,
        Dictionary<string, (NativeVariant Value, int DaoType)> priorValuesByName)
    {
        foreach ((_, ParameterObject param) in priorParams)
        {
            param.Invalidate();
        }
        ReleasePriorValues(priorValuesByName);
    }

    private static void ReleasePriorValues(Dictionary<string, (NativeVariant Value, int DaoType)> priorValuesByName)
    {
        foreach ((NativeVariant value, _) in priorValuesByName.Values)
        {
            NativeVariant dropped = value;
            SqlParameters.Release(ref dropped);
        }
        priorValuesByName.Clear();
    }

    private static CallResult Live(ComObject self, Func<QueryDefObject, CallResult> op)
    {
        var q = (QueryDefObject)self;
        return q.Invalid ? q.Fail(3420, DaoErrors.ObjectInvalid) : op(q);
    }

    private CallResult ReadType()
    {
        // r-cat-1b/qd-view-*, r-cat-1b/qd-sql-change-type-tracking, r-q39/sql-put-type-change-accdb:
        // a QueryDef created in the current session reads Type = 0 until reopen; a saved QueryDef whose SQL changes
        // family keeps its pre-change session Type until the database is closed and reopened.
        if (_mode == QueryDefMode.Saved && _sessionTypeOverride is int sessionType)
        {
            return Int16(sessionType);
        }
        if (_mode == QueryDefMode.Draft)
        {
            return Int16(0);
        }
        if (_mode == QueryDefMode.Temporary)
        {
            // r-cat-1/temporary-querydef records Type = 0 for a temporary SELECT; non-SELECT temporary Type is not recorded.
            return EffectiveFamily == QueryFamily.Select
                ? Int16(0)
                : CallResult.Fail(Failure.NotModelled("NotModelled.TemporaryQueryDefNonSelectType", "QueryDef.Type on a temporary non-SELECT QueryDef (not recorded)"));
        }
        return _query!.Family is { } f
            ? Int16((int)f)
            : CallResult.Fail(Failure.NotModelled("NotModelled.QueryFamily", $"QueryDef.Type of flag class 0x{_query.FlagClass:X2}"));
    }

    private CallResult PutName(nint value)
    {
        if (_mode == QueryDefMode.Temporary)
        {
            // r-dao-1-err-1/draft-querydef [52]: setting Name on a temporary QueryDef fails 3219 from DAO.QueryDef.
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (_mode == QueryDefMode.Saved)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefNamePut", "QueryDef.Name put on a saved QueryDef (not in stretch 1 of W-07/saved-definition)"));
        }
        if (!TryReadText(value, out string text))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        _draftName = text;
        return CallResult.Nothing;
    }

    private CallResult PutSql(nint value)
    {
        if (!TryReadText(value, out string text))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (_mode == QueryDefMode.Saved)
        {
            return Database.UpdateQueryDefSql(this, _query!, text);
        }
        _rawSql = text;
        ResetPlan();
        return CallResult.Nothing;
    }

    private CallResult PutConnect(nint value)
    {
        if (_mode == QueryDefMode.Saved)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefConnectPut", "QueryDef.Connect put on a saved QueryDef (not in stretch 1 of W-07/saved-definition)"));
        }
        if (!TryReadText(value, out string text))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        _connect = text;
        ResetPlan();
        return CallResult.Nothing;
    }

    private CallResult PutReturnsRecords(nint value)
    {
        if (_mode == QueryDefMode.Saved)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefReturnsRecordsPut", "QueryDef.ReturnsRecords put on a saved QueryDef (not in stretch 1 of W-07/saved-definition)"));
        }
        bool? flag = Args.Bool(value, omitted: true);
        if (flag is null)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (!flag.Value)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.QueryDefReturnsRecordsFalse", "QueryDef.ReturnsRecords = False (not recorded in r-q39)"));
        }
        return CallResult.Nothing;
    }

    internal static bool TryReadText(nint arg, out string text)
    {
        text = "";
        if (arg == 0)
        {
            return true;
        }
        VarType vt = Args.TypeOf(arg);
        if ((vt & ~VarType.ByRef) == VarType.Null)
        {
            return false;
        }
        if (vt == VarType.Bstr && Args.Cell(arg)->Pointer == 0)
        {
            text = "";
            return true;
        }
        if (Args.Text(arg) is { } s)
        {
            text = s;
            return true;
        }
        return false;
    }

    private void ResetPlan()
    {
        _sqlGeneration++;
        _parameters?.Release();
        _parameters = null;
        for (int i = 0; i < _values.Length; i++)
        {
            if (_values[i] is { } value)
            {
                SqlParameters.Release(ref value);
                _values[i] = null;
            }
        }
        _values = [];
        _parameterTypeMismatch = [];
        _plan?.Dispose();
        _plan = null;
        _action?.Dispose();
        _action = null;
        _planFailure = null;
        _planMade = false;
    }

    private bool HasParameterTypeMismatch()
    {
        for (int i = 0; i < _parameterTypeMismatch.Length; i++)
        {
            if (_parameterTypeMismatch[i])
            {
                return true;
            }
        }
        return false;
    }

    private CallResult Sql()
    {
        if (_mode != QueryDefMode.Saved)
        {
            return Text(_rawSql);
        }
        if (_savedSqlOverride is not null)
        {
            return Text(_savedSqlOverride);
        }
        RenderedSql sql = QuerySql.Render(_query!);
        return sql.Text is { } text ? Text(text) : CallResult.Fail(Failure.NotModelled("NotModelled.QuerySqlShape", $"QueryDef.SQL of '{_query!.Name}': {sql.NotModelled}"));
    }

    private string? EffectiveSqlText() => _mode == QueryDefMode.Saved ? QuerySql.Render(_query!).Text : _rawSql;

    /// <summary>
    /// OpenRecordset(Type, Options): the saved or temporary query's recordset, as Database.OpenRecordset opens it, with
    /// this QueryDef's parameter values (DAO-QUERY: without values 3061; d1e/query-parameter-values: pId = "2" opens
    /// row 2; r-cat-1/temporary-querydef, r-fix-wire-ado/param-select-text-fresh).
    /// </summary>
    private CallResult OpenRecordset(nint typeArg, nint optionsArg)
    {
        int? type = Args.Int32(typeArg, omitted: null);
        int? options = Args.Int32(optionsArg, omitted: 0);
        if (options is null || (typeArg != 0 && type is null))
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.OpenRecordset with a Type or Options argument that is not numeric"));
        }
        CallResult result;
        if (_mode != QueryDefMode.Saved)
        {
            if (EffectiveFamily != QueryFamily.Select)
            {
                return CallResult.Fail(Failure.NotImplemented($"QueryDef.OpenRecordset of an unsaved {EffectiveFamily?.ToString() ?? "unclassified"} query"));
            }
            Plan();
            result = SelectRoute.Open(Database, _rawSql, type, options.Value, _values);
        }
        else
        {
            BoundSelect? plan = Plan();
            if (HasParameterTypeMismatch())
            {
                return CallResult.Fail(Failure.NotModelled(
                    "NotModelled.QueryDefExecuteAfterParameterTypeChange",
                    "opening a QueryDef after QueryDef.SQL changed a surviving parameter's declared type is not recorded (r-cat-1/held-sql-replace)"));
            }
            result = plan is not null && QuerySql.Render(_query!).Text is { } text
                ? SelectRoute.Open(Database, text, type, options.Value, _values)
                : Database.OpenSaved(_query!, type, options.Value);
        }
        Opened |= result.Failure is null;
        return result;
    }

    /// <summary>Whether a recordset of this QueryDef was opened: QueryDef.Fields is recorded before an open only (SQL-META).</summary>
    public bool Opened { get; private set; }

    /// <summary>
    /// The select or action query's SQL bound once (W-06/parser-binder-scan, W-07/actions); works for both Saved
    /// (rendered via <see cref="QuerySql"/>) and Temporary/Draft (raw SQL) QueryDefs.
    /// </summary>
    public BoundSelect? Plan()
    {
        if (!_planMade)
        {
            _planMade = true;
            QueryFamily? family = EffectiveFamily;
            if (family is QueryFamily.Select or QueryFamily.Delete or QueryFamily.Update or QueryFamily.Append
                && EffectiveSqlText() is { Length: > 0 } text)
            {
                if (family == QueryFamily.Select)
                {
                    _plan = BoundSelect.Prepare(text, Database.Sql, out _planFailure);
                }
                else
                {
                    _action = BoundAction.Prepare(text, Database.Sql, out _planFailure);
                }
                int paramCount = BoundParameters?.Count ?? 0;
                _values = paramCount > 0 ? new NativeVariant?[paramCount] : [];
                _parameterTypeMismatch = paramCount > 0 ? new bool[paramCount] : [];
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
    /// QueryDef.Fields before an open: bound columns for SELECT (qualified.md SQL-META); empty (Count 0) for
    /// non-row-returning families (r-cat-1b/qd-view-update, qd-view-passthrough, qd-view-delete, qd-view-append,
    /// qd-view-make-table, qd-view-ddl).
    /// </summary>
    private CallResult FieldsCollection()
    {
        QueryFamily? family = EffectiveFamily;
        if (family is QueryFamily.Update or QueryFamily.Delete or QueryFamily.Append or QueryFamily.MakeTable
            or QueryFamily.DataDefinition or QueryFamily.PassThrough)
        {
            var empty = new DefinitionCollection(DaoTypes.Fields, "DAO.Fields", Database, [], (c, i) => throw new InvalidOperationException("no fields"))
            {
                IsOwnerInvalid = () => Invalid,
            };
            CallResult emptyResult = Object(empty);
            empty.Release();
            return emptyResult;
        }
        if (Plan() is null)
        {
            return CallResult.Fail(Failure.NotImplemented(_planFailure is { } f
                ? $"QueryDef.Fields of '{CurrentName}' whose SQL does not bind here: {f.NotModelled ?? f.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f.Message}"
                : $"QueryDef.Fields of a {family?.ToString() ?? "unclassified"} query (the binder reads SELECT; W-06, W-07)"));
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
    /// r-cat-1b/qd-execute-param-action; temporary INSERT: r-q-wire/feff-accdb-param).
    /// </summary>
    private CallResult Execute(nint optionsArg)
    {
        QueryFamily? family = EffectiveFamily;
        if (family is QueryFamily.Select or QueryFamily.Crosstab or QueryFamily.Union)
        {
            _recordsAffected = 0;
            return Fail(3065, SelectRoute.CannotExecuteSelect);
        }
        if (Args.Int32(optionsArg, omitted: 0) is not { } options)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.Execute with an Options argument that is not numeric (not recorded)"));
        }
        Plan();
        if (HasParameterTypeMismatch())
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.QueryDefExecuteAfterParameterTypeChange",
                "executing a QueryDef after QueryDef.SQL changed a surviving parameter's declared type is not recorded (r-cat-1/held-sql-replace)"));
        }
        if (_action is { } action)
        {
            return ActionRoute.Execute(Database, this, action, options, _values, ref _recordsAffected);
        }
        if (_planFailure is { } failure)
        {
            return ActionRoute.Failed(Database, this, failure, ref _recordsAffected);
        }
        if (_mode == QueryDefMode.Saved)
        {
            return ActionRoute.ExecuteSaved(Database, this, _query!, options, null, ref _recordsAffected);
        }
        return CallResult.Fail(Failure.NotImplemented($"QueryDef.Execute of an unsaved {family?.ToString() ?? "unclassified"} query"));
    }

    /// <summary>A built-in the blob stores (flag 01: ODBCTimeout, MaxRecords), else the value ACE shows for an unset one.</summary>
    private CallResult StoredOr(string name, CallResult unset)
    {
        if (_mode != QueryDefMode.Saved)
        {
            return unset;
        }
        if (_storedFailure is { } failure)
        {
            return CallResult.Fail(Failure.NotImplemented($"QueryDef.{name} when the property blob cannot be read: {failure}"));
        }
        StoredProperty? stored = _stored?.Properties.FirstOrDefault(p => p.Name == name);
        return stored is null ? unset : PropertyObject.StoredValue(stored);
    }

    /// <summary>
    /// Parameters (qualified.md R-DAO-1 DAO-QUERY, R-SQL-3 SQL-META parameters): works on both Saved and Temporary
    /// QueryDefs. Pass-through and DDL queries have none (Count 0).
    /// </summary>
    private DefinitionCollection ParametersCollection()
    {
        if (_parameters is not null)
        {
            return _parameters;
        }
        QueryFamily? family = EffectiveFamily;
        if (family is QueryFamily.PassThrough or QueryFamily.DataDefinition)
        {
            return _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [], (c, i) => throw new InvalidOperationException("no parameters"))
            {
                ClosedSource = "DAO.QueryDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
                IsOwnerInvalid = () => Invalid,
            };
        }
        Plan();
        if (BoundParameters is not { } parameters)
        {
            return _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [], (c, i) => throw new InvalidOperationException("no parameters"))
            {
                ClosedSource = "DAO.QueryDef",
                IsOwnerInvalid = () => Invalid,
                Gap = _planFailure is { } f
                    ? $"the Parameters of '{CurrentName}' whose SQL does not bind here: {f.NotModelled ?? f.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)} {f.Message}"
                    : $"the Parameters of a {family?.ToString() ?? "unclassified"} query (the binder reads SELECT; W-07/actions)",
            };
        }
        SqlFailure? declarations = _plan?.ParametersFailure ?? _action?.ParametersFailure;
        _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [.. parameters.Select(p => Unbracketed(p.Name))],
            (c, i) => new ParameterObject(c, this, parameters[i], i))
        {
            ClosedSource = "DAO.QueryDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
            IsOwnerInvalid = () => Invalid,
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
        if (_mode != QueryDefMode.Saved)
        {
            _properties ??= new PropertiesObject(this, BuiltInProperties.QueryDef);
            return Object(_properties);
        }
        if (_storedFailure is { } failure)
        {
            return CallResult.Fail(Failure.NotImplemented("QueryDef.Properties when the property blob cannot be read: " + failure));
        }
        if (Database.InheritsTableProperties(_query!))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.InheritedQueryProperties", $"the table properties '{_query!.Name}' inherits (PRES-QUERY-PROPS)"));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.QueryDef, _stored));
        return Object(_properties);
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        ResetPlan();
        if (_owner is not null)
        {
            _owner.Released(this);
            _owner.Release();
        }
        else
        {
            _database.Release();
        }
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
        .Get("Name", (self, args) => Live(self, p => Text(p._parameter.Name)))
        .Get("Type", (self, args) => Live(self, p => Int16(p._parameter.DaoType)))
        .Get("Direction", (self, args) => Live(self, p => p._parameter.Declared ? Int16(1) : CallResult.Fail(Failure.NotModelled("NotModelled.ParameterDirection", "Direction of an implicit parameter (not recorded)"))))
        .Get("Value", (self, args) => Live(self, p => p._query.ParameterValue(p._boundIndex) is { } value ? SqlResults.Of(value, "Parameter.Value") : CallResult.OfScalar(VarType.Empty, 0)))
        .Put("Value", (self, args) => { nint value = args[^1]; return Live(self, p => p.Assign(value)); });

    private readonly DefinitionCollection _owner;
    private readonly QueryDefObject _query;
    private readonly Sql.QueryParameter _parameter;
    private int _boundIndex;
    private bool _invalidated;

    public ParameterObject(DefinitionCollection owner, QueryDefObject query, Sql.QueryParameter parameter, int boundIndex) : base(Decl, Vtable, owner.Engine, "DAO.Parameter")
    {
        _owner = owner;
        _query = query;
        _parameter = parameter;
        _boundIndex = boundIndex;
        owner.AddRef();
        query.AddRef(); // its value lives on the QueryDef (CAT-V: one instance's values are shared by its Parameters)
    }

    public Sql.QueryParameter ParameterInfo => _parameter;

    public int BoundIndex => _boundIndex;

    public void Rebind(int boundIndex) => _boundIndex = boundIndex;

    /// <summary>Permanently invalidate this Parameter wrapper after a SQL rewrite drops its name (r-dao-1-err-1/query-sql-rewrite).</summary>
    public void Invalidate() => _invalidated = true;

    private CallResult Assign(nint value)
    {
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        SqlFailure? refused = SqlParameters.Adapt(_parameter, incoming, _owner.Database.Sql.Context, out NativeVariant adapted);
        if (refused is not null)
        {
            return refused.IsNotModelled ? CallResult.Fail(Failure.NotModelled(refused.NotModelled!, refused.Message)) : Fail(refused.Number, refused.Message);
        }
        _query.SetParameterValue(_boundIndex, adapted);
        return CallResult.Nothing;
    }

    public override bool Invalid => _invalidated || _query.Invalid;

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

    private IReadOnlyList<ResultColumn>? _namesFor;
    private CollectionNames? _names;

    /// <summary>A collection per Fields get (its identity is not recorded); it holds its QueryDef, whose plan its columns are.</summary>
    public QueryFieldsObject(QueryDefObject query) : base(Decl, Vtable, query.Engine, "DAO.Fields")
    {
        Query = query;
        query.AddRef();
    }

    protected override void OnFinalRelease() => Query.Release();

    public QueryDefObject Query { get; }

    public IReadOnlyList<ResultColumn> Columns => Query.Plan()?.Columns ?? [];

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
        IReadOnlyList<ResultColumn> cols = Columns;
        if (!ReferenceEquals(_namesFor, cols))
        {
            _namesFor = cols;
            _names = new CollectionNames([.. cols.Select(c => c.Name)]);
        }
        int? ordinal = CollectionLookup.Find(index, _names!, out bool objectIndex);
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
    private readonly int _generation;

    public QueryFieldObject(QueryFieldsObject fields, int ordinal) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        _generation = fields.Query.SqlGeneration;
        fields.AddRef();
    }

    private ResultColumn Column => _fields.Columns[_ordinal];

    public override bool Invalid => _fields.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<QueryFieldObject, CallResult> op)
    {
        var f = (QueryFieldObject)self;
        return QueryFieldsObject.Live(
            f,
            field => field._generation != field._fields.Query.SqlGeneration || (uint)field._ordinal >= (uint)field._fields.Columns.Count
                ? CallResult.Fail(Failure.NotModelled(
                    "NotModelled.HeldQueryFieldAfterSqlPut",
                    "accessing a held QueryDef.Field after QueryDef.SQL rewrite is not recorded (qualified.md R-CAT-1 CAT-Q r-q39)"))
                : op(field),
            f._fields.Query);
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
