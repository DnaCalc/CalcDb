using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;

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
/// Mutation hooks supplied by owners that support <c>Properties.Append</c>, <c>Properties.Delete</c>, and user
/// <c>Property.Value</c> puts (W-05, W-07, W-08 / R-CAT-1, R-DAO-1;
/// <c>evidence/r-q51-codecs-props-compact/prop-db-lifecycle-accdb</c>, <c>prop-tdf-lifecycle-accdb</c>,
/// <c>prop-fld-lifecycle-accdb</c>, <c>prop-errors-accdb</c>, and Jet 4 MDB twins).
/// </summary>
internal sealed record PropertyMutationHost(
    Func<StoredProperty, CallResult>? Append,
    Func<StoredProperty, CallResult>? Update,
    Func<string, CallResult>? Delete,
    string? DetachedAppendErrorSource = null);

/// <summary>
/// Properties (_DynaCollection; R-DAO-1 DAO-PROP, DAO-COLL): Count, Item by name or ordinal (a missing name is 3270,
/// as DAO-COLL qualified), Append, Delete, Refresh. A Property wrapper is shared while a client holds it (DAO-ID) and
/// made anew after its last release; deleting a user property invalidates any held wrapper for it (3420).
/// </summary>
internal sealed unsafe class PropertiesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Properties;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, p => Int16(p._specs.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, p => p.Item(index)); })
        .Method("Append", (self, args) => { nint obj = args[0]; return Live(self, p => p.Append(obj)); })
        .Method("Delete", (self, args) => { nint name = args[0]; return Live(self, p => p.Delete(name)); })
        .Method("Refresh", (self, args) => Live(self, p => CallResult.Nothing));

    private readonly DaoObject _owner;
    private readonly List<PropertySpec> _specs;
    private CollectionNames _names;
    private readonly Dictionary<int, PropertyObject> _held = [];
    private readonly PropertyMutationHost? _host;

    private readonly string? _entriesNotRecorded;

    public PropertiesObject(DaoObject owner, PropertySpec[] specs, PropertyMutationHost? host = null) : base(Decl, Vtable, owner.Engine, "DAO.Properties")
    {
        _owner = owner;
        _specs = [.. specs];
        _names = new CollectionNames([.. _specs.Select(s => s.Name)]);
        _host = host;
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
        var made = new PropertyObject(this, i, _specs[i]);
        _held[i] = made;
        CallResult result = Object(made);
        made.Release();
        return result;
    }

    private CallResult Append(nint objArg)
    {
        if (_host is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertiesAppend", $"Properties.Append on {_owner.Declaration.Name} is not recorded"));
        }
        if (_host.DetachedAppendErrorSource is { } detachedSource)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 168 (dtdfp.Append -> DAO.Properties)
            // and 180 (dfldp.Append -> DAO.Property): detached TableDef/Field Properties.Append fails 3219.
            return CallResult.Fail(Failure.Dao(3219, detachedSource, DaoErrors.InvalidOperation));
        }
        if (_host.Append is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertiesAppend", $"Properties.Append on {_owner.Declaration.Name} is not recorded"));
        }
        if (Args.ObjectOf<PropertyObject>(objArg) is not { } prop)
        {
            return CallResult.Fail(Failure.Dao(3421, "DAO.Properties", DaoErrors.ConversionError));
        }
        if (prop.IsInvalid)
        {
            return CallResult.Fail(Failure.Dao(3420, "DAO.Property", DaoErrors.ObjectInvalid));
        }
        if (prop.IsAttached)
        {
            return CallResult.Fail(Failure.Dao(3367, "DAO.Property", DaoErrors.AlreadyInCollection));
        }
        if (!prop.HasValue)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 97, 106, 115:
            // Append before Value is set fails 3386 from DAO.Property.
            return CallResult.Fail(Failure.Dao(3386, "DAO.Property", DaoErrors.PropertyValueRequired));
        }
        if (string.IsNullOrEmpty(prop.PropertyName))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertiesAppendEmptyName", "Properties.Append with empty Property.Name is not recorded (evidence/r-q51-codecs-props-compact/prop-errors-accdb)"));
        }
        if (_names.Find(prop.PropertyName) is not null)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 124, 133, 142:
            // Duplicate property name fails 3367 from DAO.Property.
            return CallResult.Fail(Failure.Dao(3367, "DAO.Property", DaoErrors.AlreadyInCollection));
        }
        StoredProperty stored = prop.ToStoredProperty();
        CallResult persisted = _host.Append(stored);
        if (persisted.Failure is not null)
        {
            return persisted;
        }
        int newIndex = _specs.Count;
        PropertySpec spec = PropertySpec.User(stored);
        _specs.Add(spec);
        _names = new CollectionNames([.. _specs.Select(s => s.Name)]);
        prop.AttachTo(this, newIndex, spec);
        _held[newIndex] = prop;
        return CallResult.Nothing;
    }

    private CallResult Delete(nint nameArg)
    {
        if (_host?.Delete is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertiesDelete", $"Properties.Delete on {_owner.Declaration.Name} is not recorded"));
        }
        string? name = Args.Text(nameArg);
        if (name is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertiesDeleteNonString", "Properties.Delete with null or non-string name is not recorded (evidence/r-q51-codecs-props-compact/prop-errors-accdb)"));
        }
        int? ordinal = _names.Find(name);
        if (ordinal is not int i)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 200, 209, 218, 258:
            // Properties.Delete of a missing name fails 3265 from DAO.Properties.
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        PropertySpec target = _specs[i];
        if (target.Stored is null)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 188, 192, 196:
            // Properties.Delete of a built-in property fails 3384 (0x800A0D38) from DAO.Properties.
            return Fail(3384, DaoErrors.CannotDeleteBuiltInProperty);
        }
        CallResult persisted = _host.Delete(target.Name);
        if (persisted.Failure is not null)
        {
            return persisted;
        }
        if (_held.Remove(i, out PropertyObject? deletedHeld))
        {
            deletedHeld.MarkDeleted();
        }
        _specs.RemoveAt(i);
        if (_held.Count > 0)
        {
            List<(int OldKey, PropertyObject Obj)> shifted = [];
            foreach (KeyValuePair<int, PropertyObject> kv in _held)
            {
                if (kv.Key > i)
                {
                    shifted.Add((kv.Key, kv.Value));
                }
            }
            foreach ((int oldKey, _) in shifted)
            {
                _held.Remove(oldKey);
            }
            foreach ((int oldKey, PropertyObject obj) in shifted)
            {
                int newKey = oldKey - 1;
                obj.SetIndex(newKey);
                _held[newKey] = obj;
            }
        }
        _names = new CollectionNames([.. _specs.Select(s => s.Name)]);
        return CallResult.Nothing;
    }

    internal CallResult UpdateUserProperty(int index, PropertyObject prop, StoredProperty updated)
    {
        if (_host?.Update is null)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.PropertyValuePut", $"Property.Value put on {_owner.Declaration.Name} is not recorded"));
        }
        CallResult persisted = _host.Update(updated);
        if (persisted.Failure is not null)
        {
            return persisted;
        }
        PropertySpec spec = PropertySpec.User(updated);
        _specs[index] = spec;
        prop.UpdateSpec(spec);
        return CallResult.Nothing;
    }

    public void Released(PropertyObject p)
    {
        foreach (KeyValuePair<int, PropertyObject> kv in _held.Where(kv => ReferenceEquals(kv.Value, p)).ToArray())
        {
            _held.Remove(kv.Key);
        }
    }
}

/// <summary>
/// A Property (R-DAO-1 DAO-PROP): attached (built-in or user-defined) or draft (created by <c>CreateProperty</c>
/// before <c>Properties.Append</c>). Exposes Name, Type, Value (get/put), and Inherited.
/// </summary>
internal sealed unsafe class PropertyObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Property;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, p => Text(p.PropertyName)))
        .Put("Name", (self, args) => { nint v = args[^1]; return Live(self, p => p.PutName(v)); })
        .Get("Type", (self, args) => Live(self, p => Int16(p.PropertyType)))
        .Put("Type", (self, args) => { nint v = args[^1]; return Live(self, p => p.PutType(v)); })
        // evidence/r-dao-1b/property-append-all-types calls 6, 17, 28, ...: Inherited on an unappended draft Property fails 3219 from DAO.Property.
        .Get("Inherited", (self, args) => Live(self, p => p.IsAttached ? Bool(false) : p.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("Value", (self, args) => Live(self, p => p.GetValue()))
        .Put("Value", (self, args) => { nint v = args[^1]; return Live(self, p => p.PutValue(v)); });

    private PropertiesObject? _owner;
    private int _index;
    private PropertySpec? _spec;
    private string _draftName;
    private short _draftType;
    private byte[]? _draftPayload;
    private bool _deleted;

    public PropertyObject(PropertiesObject owner, int index, PropertySpec spec) : base(Decl, Vtable, owner.Engine, "DAO.Property")
    {
        _owner = owner;
        _index = index;
        _spec = spec;
        _draftName = spec.Name;
        _draftType = spec.Type;
        owner.AddRef();
    }

    private PropertyObject(EngineState engine, string name, short type, byte[]? payload) : base(Decl, Vtable, engine, "DAO.Property")
    {
        _draftName = name;
        _draftType = type;
        _draftPayload = payload;
    }

    public bool IsAttached => _owner is not null;

    public bool IsInvalid => _deleted || (_owner is not null && _owner.Owner.Invalid);

    public bool HasValue => _spec is not null || _draftPayload is not null;

    public string PropertyName => _spec is not null ? _spec.Name : _draftName;

    public short PropertyType => _spec is not null ? _spec.Type : _draftType;

    protected override OperationTable Table => Operations;

    internal void MarkDeleted() => _deleted = true;

    internal void SetIndex(int index) => _index = index;

    internal void UpdateSpec(PropertySpec spec) => _spec = spec;

    internal void AttachTo(PropertiesObject owner, int index, PropertySpec spec)
    {
        _owner = owner;
        _index = index;
        _spec = spec;
        _draftPayload = null;
        owner.AddRef();
    }

    internal StoredProperty ToStoredProperty() =>
        new(_draftName, Flags: 0x00, NativeType: (byte)_draftType, Payload: _draftPayload ?? []);

    private static bool IsArgMissing(nint arg) =>
        arg == 0
        || (Args.TypeOf(arg) == VarType.Error && (int)Args.Cell(arg)->Int64 == unchecked((int)0x80020004));

    private static bool IsArgOmitted(nint arg) =>
        IsArgMissing(arg)
        || Args.TypeOf(arg) == VarType.Empty;

    private static bool IsArgNull(nint arg) => arg != 0 && Args.TypeOf(arg) == VarType.Null;

    private static bool IsArgEmpty(nint arg) => arg == 0 || Args.TypeOf(arg) == VarType.Empty;

    /// <summary>
    /// <c>CreateProperty([Name], [Type], [Value], [DDL])</c> (R-DAO-1 DAO-PROP;
    /// <c>evidence/r-q51-codecs-props-compact/prop-db-lifecycle-accdb</c>, <c>prop-tdf-lifecycle-accdb</c>,
/// <c>prop-fld-lifecycle-accdb</c>, <c>prop-errors-accdb</c>, and Jet 4 MDB twins).
    /// Rejects Null or empty-string Value with 3385 from <c>DAO.Property</c>.
    /// </summary>
    public static CallResult CreateDraft(EngineState engine, nint nameArg, nint typeArg, nint valueArg, nint ddlArg)
    {
        string name = "";
        if (!IsArgOmitted(nameArg))
        {
            if (IsArgNull(nameArg))
            {
                return CallResult.Fail(Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError));
            }
            if (Args.Text(nameArg) is not { } parsedName)
            {
                return CallResult.Fail(Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError));
            }
            name = parsedName;
        }

        short type = 0;
        if (!IsArgOmitted(typeArg))
        {
            if (IsArgNull(typeArg) || Args.Int32(typeArg, omitted: null) is not int rawType)
            {
                return CallResult.Fail(Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError));
            }
            if (rawType == 16)
            {
                // evidence/r-dao-1b/property-append-all-types call 148: CreateProperty(..., 16 (dbBigInt), ...) fails 3035 from DAO.Property.
                return CallResult.Fail(Failure.Dao(3035, "DAO.Property", "System resource exceeded."));
            }
            if (!IsSupportedUserPropertyType(rawType))
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreatePropertyType", $"CreateProperty with Type={rawType} is not recorded (evidence/r-dao-1b/property-append-all-types)"));
            }
            type = (short)rawType;
        }

        if (!IsArgOmitted(ddlArg))
        {
            if (Args.Bool(ddlArg, omitted: false) is true)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreatePropertyDdl", "CreateProperty with DDL:=True is not recorded (evidence/r-q51-codecs-props-compact)"));
            }
        }

        byte[]? payload = null;
        if (!IsArgMissing(valueArg))
        {
            if (type == 0)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.CreatePropertyUntypedValue", "CreateProperty with Value but omitted Type is not recorded"));
            }
            if (!TryEncodePropertyValue(type, valueArg, out payload, out Failure? encodeFailure))
            {
                return CallResult.Fail(encodeFailure!);
            }
        }

        var draft = new PropertyObject(engine, name, type, payload);
        CallResult result = Object(draft);
        draft.Release();
        return result;
    }

    private static bool IsSupportedUserPropertyType(int daoType) => daoType is
        NativeTypes.Boolean or NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or
        NativeTypes.Currency or NativeTypes.Single or NativeTypes.Double or NativeTypes.DateTime or
        NativeTypes.Binary or NativeTypes.Text or NativeTypes.Ole or NativeTypes.Memo or
        NativeTypes.Guid or 20; // dbDecimal (20)

    private static CallResult Live(ComObject self, Func<PropertyObject, CallResult> op)
    {
        var p = (PropertyObject)self;
        return p.IsInvalid ? p.Fail(3420, DaoErrors.ObjectInvalid) : op(p);
    }

    private CallResult PutName(nint arg)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.Text(arg) is not { } s)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        _draftName = s;
        return CallResult.Nothing;
    }

    private CallResult PutType(nint arg)
    {
        if (IsAttached)
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 148, 153, 158:
            // Type put on an appended Property fails 3219 from DAO.Property.
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.Int32(arg, omitted: null) is not int rawType)
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (rawType == 16)
        {
            // evidence/r-dao-1b/property-append-all-types call 148: dbBigInt (16) fails 3035 from DAO.Property.
            return Fail(3035, "System resource exceeded.");
        }
        if (!IsSupportedUserPropertyType(rawType))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CreatePropertyType", $"Property.Type put = {rawType} is not recorded (evidence/r-dao-1b/property-append-all-types)"));
        }
        _draftType = (short)rawType;
        return CallResult.Nothing;
    }

    private CallResult PutValue(nint arg)
    {
        if (!IsAttached)
        {
            if (_draftType == 0)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DraftPropertyUntypedValuePut", "Property.Value put on a draft Property with Type=0 is not recorded"));
            }
            if (!TryEncodePropertyValue(_draftType, arg, out byte[]? payload, out Failure? encodeFailure))
            {
                return CallResult.Fail(encodeFailure!);
            }
            _draftPayload = payload;
            return CallResult.Nothing;
        }

        if (_spec!.Stored is { } stored)
        {
            if (!TryEncodePropertyValue(_spec.Type, arg, out byte[]? payload, out Failure? encodeFailure))
            {
                return CallResult.Fail(encodeFailure!);
            }
            var updated = new StoredProperty(stored.Name, stored.Flags, stored.NativeType, payload!);
            return _owner!.UpdateUserProperty(_index, this, updated);
        }

        return CallResult.Fail(Failure.NotModelled("NotModelled.BuiltInPropertyValuePut", $"Property.Value put on built-in property '{_spec.Name}' of {_owner!.Owner.Declaration.Name} is not recorded"));
    }

    private CallResult GetValue()
    {
        if (!IsAttached)
        {
            if (_draftPayload is null)
            {
                // evidence/r-dao-1-err-1/prop-user-lifecycle calls 11, 16: Value on an unvalued draft Property returns VT_NULL.
                return CallResult.Of(OwnedVariant.Null());
            }
            return StoredValue(ToStoredProperty());
        }
        if (_spec!.Stored is { } stored)
        {
            return StoredValue(stored);
        }
        DaoObject owner = _owner!.Owner;
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

    /// <summary>Encode a COM VARIANT to an MR2 <see cref="StoredProperty"/> payload for <paramref name="daoType"/>.</summary>
    private static bool TryEncodePropertyValue(short daoType, nint arg, out byte[]? payload, out Failure? failure)
    {
        payload = null;
        failure = null;
        if (IsArgNull(arg) || IsArgEmpty(arg))
        {
            // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 66, 75: Null fails 3385 from DAO.Property.
            failure = Failure.Dao(3385, "DAO.Property", DaoErrors.UserPropertyNoNull);
            return false;
        }

        switch (daoType)
        {
            case NativeTypes.Boolean:
                if (Args.Bool(arg, omitted: false) is bool b)
                {
                    payload = [b ? (byte)0xFF : (byte)0x00];
                    return true;
                }
                if (Args.Int32(arg, omitted: null) is int bi)
                {
                    payload = [bi != 0 ? (byte)0xFF : (byte)0x00];
                    return true;
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Byte:
                if (Args.Int32(arg, omitted: null) is int u8 && u8 is >= byte.MinValue and <= byte.MaxValue)
                {
                    payload = [(byte)u8];
                    return true;
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Integer:
                if (Args.Int32(arg, omitted: null) is int i16 && i16 is >= short.MinValue and <= short.MaxValue)
                {
                    payload = new byte[2];
                    BinaryPrimitives.WriteInt16LittleEndian(payload, (short)i16);
                    return true;
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Long:
                if (Args.Int32(arg, omitted: null) is int i32)
                {
                    payload = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, i32);
                    return true;
                }
                if (Args.Text(arg) is { } lText && int.TryParse(lText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLong))
                {
                    payload = new byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(payload, parsedLong);
                    return true;
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Currency:
                using (OwnedVariant? cyVar = OwnedVariant.ChangeType(Args.Cell(arg), VarType.Currency, SessionLocale.Lcid, out _))
                {
                    if (cyVar is not null)
                    {
                        payload = new byte[8];
                        BinaryPrimitives.WriteInt64LittleEndian(payload, cyVar.Pointer->Int64);
                        return true;
                    }
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Single:
                using (OwnedVariant? r4Var = OwnedVariant.ChangeType(Args.Cell(arg), VarType.R4, SessionLocale.Lcid, out _))
                {
                    if (r4Var is not null)
                    {
                        payload = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(payload, (int)r4Var.Pointer->Int64);
                        return true;
                    }
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Double:
                using (OwnedVariant? r8Var = OwnedVariant.ChangeType(Args.Cell(arg), VarType.R8, SessionLocale.Lcid, out _))
                {
                    if (r8Var is not null)
                    {
                        payload = new byte[8];
                        BinaryPrimitives.WriteInt64LittleEndian(payload, r8Var.Pointer->Int64);
                        return true;
                    }
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.DateTime:
                using (OwnedVariant? dtVar = OwnedVariant.ChangeType(Args.Cell(arg), VarType.Date, SessionLocale.Lcid, out _))
                {
                    if (dtVar is not null)
                    {
                        payload = new byte[8];
                        BinaryPrimitives.WriteInt64LittleEndian(payload, dtVar.Pointer->Int64);
                        return true;
                    }
                }
                failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                return false;

            case NativeTypes.Text or NativeTypes.Memo:
                if (Args.Text(arg) is not { } text)
                {
                    if (Args.Int32(arg, omitted: null) is int numText)
                    {
                        text = numText.ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        failure = Failure.Dao(3421, "DAO.Property", DaoErrors.ConversionError);
                        return false;
                    }
                }
                if (text.Length == 0)
                {
                    // evidence/r-q51-codecs-props-compact/prop-errors-accdb, prop-errors-mdb calls 57, 84:
                    // Empty string for Text/Memo user property fails 3385 from DAO.Property.
                    failure = Failure.Dao(3385, "DAO.Property", DaoErrors.UserPropertyNoNull);
                    return false;
                }
                payload = Encoding.Unicode.GetBytes(text);
                return true;

            default:
                failure = Failure.NotModelled("NotModelled.PropertyEncodeType", $"encoding DAO user property of type {daoType} is not recorded");
                return false;
        }
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
        if (_owner is not null)
        {
            _owner.Released(this);
            _owner.Release();
        }
    }
}
