using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;

namespace CalcDb.Com.Dao;

/// <summary>
/// A saved QueryDef of a Database (II-06 §§8–11; R-CAT-1 CAT-Q, qualified), read from its catalog row and MSysQueries
/// role rows. Name, Type (the masked flag class), SQL (rendered as ACE renders it at open, <see cref="QuerySql"/>),
/// Connect (the pass-through type row's Name1, else ""), ReturnsRecords (True for every saved family in
/// r-cat-1/querydefs), Updatable (the session's: True on a read-write open, False read-only; r-fix-cursor-dao/updatable-members-rw),
/// the dates, the stored ODBCTimeout and MaxRecords, Parameters and Properties. Its Fields and the implicit parameters
/// need the binder (W-06/parser-binder-scan) and are named gaps. Close of a saved QueryDef changes nothing (DAO-LIFE:
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
        .Get("RecordsAffected", (self, args) => Live(self, q => Int32(0)))
        .Get("StillExecuting", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("CacheSize", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Prepare", (self, args) => Live(self, q => q.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Parameters", (self, args) => Live(self, q => Object(q.ParametersCollection())))
        .Get("Fields", (self, args) => Live(self, q => CallResult.Fail(Failure.NotImplemented("QueryDef.Fields (the output columns need the binder, W-06/parser-binder-scan)"))))
        .Get("Properties", (self, args) => Live(self, q => q.PropertiesCollection()))
        .Method("Close", (self, args) => Live(self, q => CallResult.Nothing));

    private readonly DefinitionCollection _owner;
    private readonly SavedQuery _query;
    private readonly PropertyMap? _stored;
    private DefinitionCollection? _parameters;
    private PropertiesObject? _properties;

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
    /// The declared parameters in declaration order (DAO-QUERY: Name and Type). A local query may also have implicit
    /// parameters (r-cat-1/querydefs QImplicit: pMin, Type 10, from an unresolved identifier); only the binder can say
    /// there are none, so the membership of a local query is a named gap. A pass-through query has none (QPass: Count 0).
    /// </summary>
    private DefinitionCollection ParametersCollection()
    {
        if (_parameters is null)
        {
            IReadOnlyList<QueryParameter> declared = _query.DeclaredParameters;
            _parameters = new DefinitionCollection(DaoTypes.Parameters, "DAO.Parameters", Database, [.. declared.Select(p => p.Name)],
                (c, i) => new ParameterObject(c, declared[i]))
            {
                Gap = _query.Family == QueryFamily.PassThrough ? null : "the Parameters of a local QueryDef (implicit parameters need the binder, W-06/parser-binder-scan)",
            };
        }
        return _parameters;
    }

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
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>A declared Parameter (DAO-QUERY): Name and Type (the declaration's native type code, which is its DAO type).</summary>
internal sealed unsafe class ParameterObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Parameter;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, p => Text(p._parameter.Name)))
        .Get("Type", (self, args) => Live(self, p => Int16(p._parameter.NativeType)));

    private readonly DefinitionCollection _owner;
    private readonly QueryParameter _parameter;

    public ParameterObject(DefinitionCollection owner, QueryParameter parameter) : base(Decl, Vtable, owner.Engine, "DAO.Parameter")
    {
        _owner = owner;
        _parameter = parameter;
        owner.AddRef();
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
    }
}

/// <summary>The catalog DateCreate and DateUpdate bits as VT_DATE, exactly (TableDef, QueryDef, Document dates).</summary>
internal static class CatalogDates
{
    public static CallResult Date(ulong? bits) => bits is ulong b
        ? CallResult.OfScalar(Foundation.VarType.Date, (long)b)
        : CallResult.Fail(Failure.NotImplemented("a catalog date of a row without that field"));
}
