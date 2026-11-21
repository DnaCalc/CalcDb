using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation;

namespace CalcDb.Com.Dao;

/// <summary>
/// One entry of a DAO Properties collection (II-06 §7: a semantic view, not the stored blob). A built-in reads through
/// its owner's own getter of the same name, so Properties("X").Value and the direct getter cannot drift apart
/// (R-DAO-1 DAO-PROP: "direct getters equal Properties.Item(...).Value"), failure and source included (a TableDef
/// field's Value without its IErrorInfo); a stored
/// (user-defined) one decodes its MR2 record (CAT-P). <see cref="Member"/> is the getter's declared name when it differs
/// from the property's spelling (a Recordset lists "BookMark" for its Bookmark getter).
/// </summary>
internal sealed record PropertySpec(string Name, short Type, StoredProperty? Stored = null, string? Member = null, bool WriteOnly = false, Func<DaoObject, CallResult>? Read = null)
{
    public static PropertySpec BuiltIn(string name, short type, string? member = null) => new(name, type, null, member);

    /// <summary>A built-in whose Properties("X").Value is supplied directly by its owner (used where the direct getter is not yet modelled).</summary>
    public static PropertySpec BuiltIn(string name, short type, Func<DaoObject, CallResult> read) => new(name, type, Read: read);

    /// <summary>A built-in with a put and no get (DBEngine DefaultUser, DefaultPassword): its Value is 3251 from the owner.</summary>
    public static PropertySpec PutOnly(string name, short type) => new(name, type, WriteOnly: true);

    public static PropertySpec User(StoredProperty stored) => new(stored.Name, stored.NativeType, stored);
}

/// <summary>
/// The built-in property inventories, each in the order ACE enumerates it, with each property's DAO type (R-DAO-1
/// DAO-PROP, qualified; records named per list). A stored property of a built-in's name is that built-in's stored value
/// (the QueryDef blobs store ODBCTimeout and MaxRecords with flag 01, r-dao-1b rich fixture), not a second entry; any
/// other stored property of the object's own map follows the built-ins in stored order (r-dao-1b/db-properties-enumerated-accdb:
/// UPText … UPMemo after Connection; tabledef-properties-enumerated: Description after ReplicaFilter).
/// </summary>
internal static class BuiltInProperties
{
    private const short Boolean = 1, Integer = 3, Long = 4, Single = 6, Date = 8, Text = 10, Memo = 12, Guid = 15, Object = 0;

    /// <summary>r-dao-1b/eng-properties-enumerated.</summary>
    public static readonly PropertySpec[] Engine =
    [
        PropertySpec.BuiltIn("Version", Memo), PropertySpec.BuiltIn("LoginTimeout", Integer), PropertySpec.BuiltIn("IniPath", Memo),
        PropertySpec.PutOnly("DefaultUser", Memo), PropertySpec.PutOnly("DefaultPassword", Memo), PropertySpec.BuiltIn("DefaultType", Long),
        PropertySpec.BuiltIn("SystemDB", Memo),
    ];

    /// <summary>r-dao-1b/ws-properties-enumerated.</summary>
    public static readonly PropertySpec[] Workspace =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("UserName", Memo), PropertySpec.BuiltIn("IsolateODBCTrans", Boolean),
        PropertySpec.BuiltIn("LoginTimeout", Long), PropertySpec.BuiltIn("DefaultCursorDriver", Long), PropertySpec.BuiltIn("Type", Long),
    ];

    /// <summary>r-dao-1b/db-properties-enumerated-accdb (the 11 built-ins before the stored ones).</summary>
    public static readonly PropertySpec[] Database =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Connect", Memo), PropertySpec.BuiltIn("Transactions", Boolean),
        PropertySpec.BuiltIn("Updatable", Boolean), PropertySpec.BuiltIn("CollatingOrder", Integer), PropertySpec.BuiltIn("QueryTimeout", Integer),
        PropertySpec.BuiltIn("Version", Memo), PropertySpec.BuiltIn("RecordsAffected", Long), PropertySpec.BuiltIn("ReplicaID", Guid),
        PropertySpec.BuiltIn("DesignMasterID", Guid), PropertySpec.BuiltIn("Connection", Object),
    ];

    /// <summary>r-dao-1b/tabledef-properties-enumerated.</summary>
    public static readonly PropertySpec[] TableDef =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Updatable", Boolean), PropertySpec.BuiltIn("DateCreated", Date),
        PropertySpec.BuiltIn("LastUpdated", Date), PropertySpec.BuiltIn("Connect", Memo), PropertySpec.BuiltIn("Attributes", Long),
        PropertySpec.BuiltIn("SourceTableName", Memo), PropertySpec.BuiltIn("RecordCount", Long), PropertySpec.BuiltIn("ValidationRule", Memo),
        PropertySpec.BuiltIn("ValidationText", Memo), PropertySpec.BuiltIn("ConflictTable", Memo), PropertySpec.BuiltIn("ReplicaFilter", Memo),
    ];

    /// <summary>r-cat-1/querydefs (every query of the fixture lists these 14).</summary>
    public static readonly PropertySpec[] QueryDef =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("DateCreated", Date), PropertySpec.BuiltIn("LastUpdated", Date),
        PropertySpec.BuiltIn("Type", Integer), PropertySpec.BuiltIn("SQL", Memo), PropertySpec.BuiltIn("Updatable", Boolean),
        PropertySpec.BuiltIn("Connect", Memo), PropertySpec.BuiltIn("ReturnsRecords", Boolean), PropertySpec.BuiltIn("ODBCTimeout", Integer),
        PropertySpec.BuiltIn("RecordsAffected", Long), PropertySpec.BuiltIn("MaxRecords", Long), PropertySpec.BuiltIn("StillExecuting", Boolean),
        PropertySpec.BuiltIn("CacheSize", Long), PropertySpec.BuiltIn("Prepare", Boolean),
    ];

    /// <summary>r-cat-1b/index-properties-enum.</summary>
    public static readonly PropertySpec[] Index =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Unique", Boolean), PropertySpec.BuiltIn("Clustered", Boolean),
        PropertySpec.BuiltIn("Primary", Boolean), PropertySpec.BuiltIn("Foreign", Boolean), PropertySpec.BuiltIn("Required", Boolean),
        PropertySpec.BuiltIn("IgnoreNulls", Boolean), PropertySpec.BuiltIn("DistinctCount", Long),
    ];

    /// <summary>r-dao-1b/containers-properties-enumerated.</summary>
    public static readonly PropertySpec[] Container =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Owner", Memo), PropertySpec.BuiltIn("UserName", Memo),
        PropertySpec.BuiltIn("Permissions", Long), PropertySpec.BuiltIn("AllPermissions", Long), PropertySpec.BuiltIn("Inherit", Boolean),
    ];

    /// <summary>r-dao-1b/documents-properties-enumerated.</summary>
    public static readonly PropertySpec[] Document =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Owner", Memo), PropertySpec.BuiltIn("UserName", Memo),
        PropertySpec.BuiltIn("Permissions", Long), PropertySpec.BuiltIn("AllPermissions", Long), PropertySpec.BuiltIn("Container", Memo),
        PropertySpec.BuiltIn("DateCreated", Date), PropertySpec.BuiltIn("LastUpdated", Date),
    ];

    /// <summary>r-dao-1b/relation-properties-enumerated.</summary>
    public static readonly PropertySpec[] Relation =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("Table", Memo), PropertySpec.BuiltIn("ForeignTable", Memo),
        PropertySpec.BuiltIn("Attributes", Long), PropertySpec.BuiltIn("PartialReplica", Boolean),
    ];

    /// <summary>
    /// A TableDef field's 24 built-ins (r-dao-1b/field-properties-enumerated). Value, Precision and Scale take the
    /// field's own DAO type as their property Type (Long field: 4, Text: 10, Memo: 12 in that record), a subtle ACE
    /// rule kept here as <paramref name="fieldType"/>.
    /// </summary>
    public static PropertySpec[] TableDefField(short fieldType) =>
    [
        PropertySpec.BuiltIn("Value", fieldType), PropertySpec.BuiltIn("Attributes", Long), PropertySpec.BuiltIn("CollatingOrder", Integer),
        PropertySpec.BuiltIn("Type", Integer), PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("OrdinalPosition", Integer),
        PropertySpec.BuiltIn("Size", Long), PropertySpec.BuiltIn("SourceField", Memo), PropertySpec.BuiltIn("SourceTable", Memo),
        PropertySpec.BuiltIn("ValidateOnSet", Boolean), PropertySpec.BuiltIn("DataUpdatable", Boolean), PropertySpec.BuiltIn("ForeignName", Memo),
        PropertySpec.BuiltIn("DefaultValue", Memo), PropertySpec.BuiltIn("ValidationRule", Memo), PropertySpec.BuiltIn("ValidationText", Memo),
        PropertySpec.BuiltIn("Required", Boolean), PropertySpec.BuiltIn("AllowZeroLength", Boolean), PropertySpec.BuiltIn("AppendOnly", Boolean),
        PropertySpec.BuiltIn("Expression", Memo), PropertySpec.BuiltIn("FieldSize", Long), PropertySpec.BuiltIn("OriginalValue", Long),
        PropertySpec.BuiltIn("VisibleValue", Long), PropertySpec.BuiltIn("Precision", fieldType), PropertySpec.BuiltIn("Scale", fieldType),
    ];

    /// <summary>A Recordset's 32 built-ins, the same list on table-type and dynaset (r-dao-1b/recordset-properties-table, -dynaset).</summary>
    public static readonly PropertySpec[] Recordset =
    [
        PropertySpec.BuiltIn("Name", Memo), PropertySpec.BuiltIn("BOF", Boolean), PropertySpec.BuiltIn("BookMark", Memo, "Bookmark"),
        PropertySpec.BuiltIn("Bookmarkable", Boolean), PropertySpec.BuiltIn("DateCreated", Date), PropertySpec.BuiltIn("EOF", Boolean),
        PropertySpec.BuiltIn("Filter", Memo), PropertySpec.BuiltIn("Index", Memo), PropertySpec.BuiltIn("LastModified", Memo),
        PropertySpec.BuiltIn("LastUpdated", Date), PropertySpec.BuiltIn("LockEdits", Boolean), PropertySpec.BuiltIn("NoMatch", Boolean),
        PropertySpec.BuiltIn("RecordCount", Long), PropertySpec.BuiltIn("Sort", Memo), PropertySpec.BuiltIn("Transactions", Boolean),
        PropertySpec.BuiltIn("Type", Integer), PropertySpec.BuiltIn("Updatable", Boolean), PropertySpec.BuiltIn("Restartable", Boolean),
        PropertySpec.BuiltIn("CacheStart", Memo), PropertySpec.BuiltIn("CacheSize", Long), PropertySpec.BuiltIn("EditMode", Integer),
        PropertySpec.BuiltIn("PercentPosition", Single), PropertySpec.BuiltIn("ValidationRule", Memo), PropertySpec.BuiltIn("ValidationText", Memo),
        PropertySpec.BuiltIn("AbsolutePosition", Long), PropertySpec.BuiltIn("StillExecuting", Boolean), PropertySpec.BuiltIn("Connection", Object),
        PropertySpec.BuiltIn("BatchCollisionCount", Long), PropertySpec.BuiltIn("BatchCollisions", Object), PropertySpec.BuiltIn("BatchSize", Long),
        PropertySpec.BuiltIn("RecordStatus", Integer), PropertySpec.BuiltIn("UpdateOptions", Long),
    ];

    /// <summary>The built-ins, then the object map's stored properties that are not built-in names, in stored order.</summary>
    public static PropertySpec[] With(PropertySpec[] builtIns, PropertyMap? stored)
    {
        if (stored is null)
        {
            return builtIns;
        }
        var specs = new List<PropertySpec>(builtIns);
        foreach (StoredProperty p in stored.Properties)
        {
            if (!builtIns.Any(b => string.Equals(b.Name, p.Name, StringComparison.OrdinalIgnoreCase)))
            {
                specs.Add(PropertySpec.User(p));
            }
        }
        return [.. specs];
    }
}

/// <summary>
/// Properties (_DynaCollection; R-DAO-1 DAO-PROP, DAO-COLL): Count, Item by name or ordinal (a missing name is 3270,
/// as DAO-COLL qualified), Refresh. The inventory is fixed when the collection is made, once per owner. A Property
/// wrapper is shared while a client holds it (DAO-ID) and made anew after its last release.
/// </summary>
internal sealed unsafe class PropertiesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Properties;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, p => Int16(p._specs.Length)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, p => p.Item(index)); })
        .Method("Refresh", (self, args) => Live(self, p => CallResult.Nothing));

    private readonly DaoObject _owner;
    private readonly PropertySpec[] _specs;
    private readonly CollectionNames _names;
    private readonly Dictionary<int, PropertyObject> _held = [];

    private readonly string? _entriesNotRecorded;

    public PropertiesObject(DaoObject owner, PropertySpec[] specs) : base(Decl, Vtable, owner.Engine, "DAO.Properties")
    {
        _owner = owner;
        _specs = specs;
        _names = new CollectionNames([.. specs.Select(s => s.Name)]);
    }

    /// <summary>
    /// A collection whose records show only its Count (<paramref name="count"/>): Item is named with
    /// <paramref name="entriesNotRecorded"/>, as the entries' names and types are not recorded.
    /// </summary>
    public PropertiesObject(DaoObject owner, int count, string entriesNotRecorded) : this(owner, [.. Enumerable.Range(0, count).Select(i => new PropertySpec($"#{i}", 0))])
    {
        _entriesNotRecorded = entriesNotRecorded;
    }

    public DaoObject Owner => _owner;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<PropertiesObject, CallResult> op)
    {
        var p = (PropertiesObject)self;
        return p._owner.Invalid ? p.Fail(3420, DaoErrors.ObjectInvalid) : op(p);
    }

    private CallResult Item(nint index)
    {
        if (_entriesNotRecorded is { } why)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertyEntries", why));
        }
        int? ordinal = CollectionLookup.Find(index, _names, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Properties.Item with an object index"));
        }
        if (ordinal is not int i)
        {
            return Fail(3270, DaoErrors.PropertyNotFound);
        }
        if (_held.TryGetValue(i, out PropertyObject? held))
        {
            return Object(held);
        }
        var made = new PropertyObject(this, _specs[i]);
        _held[i] = made;
        CallResult result = Object(made);
        made.Release();
        return result;
    }

    public void Released(PropertyObject p)
    {
        foreach (KeyValuePair<int, PropertyObject> kv in _held.Where(kv => ReferenceEquals(kv.Value, p)).ToArray())
        {
            _held.Remove(kv.Key);
        }
    }
}

/// <summary>A Property (R-DAO-1 DAO-PROP): Name, Type, Value, Inherited (False for every property of the owners built here).</summary>
internal sealed unsafe class PropertyObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Property;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, p => Text(p._spec.Name)))
        .Get("Type", (self, args) => Live(self, p => Int16(p._spec.Type)))
        .Get("Inherited", (self, args) => Live(self, p => Bool(false)))
        .Get("Value", (self, args) => Live(self, p => p.Value()));

    private readonly PropertiesObject _owner;
    private readonly PropertySpec _spec;

    public PropertyObject(PropertiesObject owner, PropertySpec spec) : base(Decl, Vtable, owner.Engine, "DAO.Property")
    {
        _owner = owner;
        _spec = spec;
        owner.AddRef();
    }

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<PropertyObject, CallResult> op)
    {
        var p = (PropertyObject)self;
        return p._owner.Owner.Invalid ? p.Fail(3420, DaoErrors.ObjectInvalid) : op(p);
    }

    private CallResult Value()
    {
        if (_spec.Stored is { } stored)
        {
            return StoredValue(stored);
        }
        DaoObject owner = _owner.Owner;
        if (_spec.WriteOnly)
        {
            return owner.Fail(3251, DaoErrors.NotSupportedForType);
        }
        if (_spec.Read is { } read)
        {
            return read(owner);
        }
        string member = _spec.Member ?? _spec.Name;
        MemberDecl? getter = owner.Declaration.Members.FirstOrDefault(m => m.Kind == InvokeKind.PropertyGet && m.Parameters.Count == 0
            && string.Equals(m.Name, member, StringComparison.OrdinalIgnoreCase));
        if (getter is null)
        {
            return CallResult.Fail(Failure.NotImplemented($"the {_spec.Name} property of {owner.Declaration.Name} (no getter declared)"));
        }
        // A TableDef field's Value getter publishes an IErrorInfo, its Properties("Value").Value does not (r-cat-1b/
        // field-properties-enum, r-dao-1b/field-properties-enumerated, r-fix-fmt-sort/fmt2-scalar-admission-decimal); no
        // record shows another built-in's failure through Properties, so the others answer as their getter.
        CallResult result = owner.Run(getter, []);
        return owner is DefinitionFieldObject && _spec.Name == "Value" && result.Failure is { PublishesErrorInfo: true } failure
            ? CallResult.Fail(failure with { PublishesErrorInfo = false })
            : result;
    }

    /// <summary>A stored property's value (CAT-P), typed by its native type code, which is also its DAO type.</summary>
    public static CallResult StoredValue(StoredProperty stored)
    {
        PropertyValue v = PropertyBlob.Decode(stored);
        if (v.Failure is { } failure)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertyPayload", failure));
        }
        return stored.NativeType switch
        {
            NativeTypes.Boolean => Bool(v.Boolean),
            NativeTypes.Byte => CallResult.OfScalar(VarType.UI1, v.Integer),
            NativeTypes.Integer => Int16((int)v.Integer),
            NativeTypes.Long => Int32((int)v.Integer),
            NativeTypes.Single => CallResult.OfScalar(VarType.R4, (long)v.Bits),
            NativeTypes.Double => CallResult.OfScalar(VarType.R8, (long)v.Bits),
            NativeTypes.DateTime => CallResult.OfScalar(VarType.Date, (long)v.Bits),
            NativeTypes.Currency => CallResult.OfScalar(VarType.Currency, (long)v.Bits),
            NativeTypes.Text or NativeTypes.Memo => Text(v.Text!),
            _ => CallResult.Fail(Failure.NotModelled("NotModelled.PropertyPayload", $"a stored property of native type {stored.NativeType} as a VARIANT")),
        };
    }

    protected override void OnFinalRelease()
    {
        _owner.Released(this);
        _owner.Release();
    }
}
