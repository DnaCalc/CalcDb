using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Container (R-DAO-1 DAO-META, qualified): one of the catalog's container rows under the top-level parent
/// (Databases, Relationships, Tables). Owner "", UserName the session's, Inherit True; Permissions and AllPermissions
/// give 3358 on every read before a put has been tried, and a put is a security change CalcDb refuses by name (DAO-SEC),
/// so they give 3358 here always. Documents are its child rows in name order, without the rows TableDefs hides.
/// </summary>
internal sealed unsafe class ContainerObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Container;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, c => Text(c._entry.Name)))
        .Get("Owner", (self, args) => Live(self, c => Text("")))
        .Get("UserName", (self, args) => Live(self, c => Text(c.Database.Workspace.UserName)))
        .Get("Inherit", (self, args) => Live(self, c => Bool(true)))
        .Get("Permissions", (self, args) => Live(self, c => c.Fail(3358, DaoErrors.NoWorkgroupFile)))
        .Get("AllPermissions", (self, args) => Live(self, c => c.Fail(3358, DaoErrors.NoWorkgroupFile)))
        .Put("Permissions", (self, args) => Live(self, c => NamedSecurityRefusal()))
        .Get("Documents", (self, args) => Live(self, c => Object(c.DocumentsCollection())))
        .Get("Properties", (self, args) => Live(self, c => Object(c._properties ??= new PropertiesObject(c, BuiltInProperties.Container))));

    private readonly DefinitionCollection _owner;
    private readonly CatalogEntry _entry;
    private DefinitionCollection? _documents;
    private PropertiesObject? _properties;

    public ContainerObject(DefinitionCollection owner, CatalogEntry entry) : base(Decl, Vtable, owner.Engine, "DAO.Container")
    {
        _owner = owner;
        _entry = entry;
        owner.AddRef();
    }

    private DatabaseObject Database => _owner.Database;

    public override bool Invalid => Database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<ContainerObject, CallResult> op)
    {
        var c = (ContainerObject)self;
        return c.Invalid ? c.Fail(3420, DaoErrors.ObjectInvalid) : op(c);
    }

    public static CallResult NamedSecurityRefusal() =>
        CallResult.Fail(Failure.NotModelled("NotModelled.Security", "a Permissions put (user-level security is excluded; DAO-SEC)"));

    private DefinitionCollection DocumentsCollection()
    {
        if (_documents is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, _) = DocumentsMembership();
            _documents = new DefinitionCollection(DaoTypes.Documents, "DAO.Documents", Database, names, make)
            {
                Membership = DocumentsMembership,
            };
        }
        return _documents;
    }

    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) DocumentsMembership()
    {
        CatalogEntry[] documents = [.. Database.Catalog.Entries.Where(e => e.ParentId == _entry.Id && (e.Flags & DatabaseObject.HiddenFromTableDefs) == 0)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
        return ([.. documents.Select(e => e.Name)], (c, i) => new DocumentObject(c, this, documents[i]), null);
    }

    public string Name => _entry.Name;

    protected override void OnFinalRelease()
    {
        _documents?.Release();
        _properties?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Document (DAO-META): a catalog row of its container (a table, a saved query, a relationship, MSysDb). Owner "",
/// UserName the session's, Container its container's name, the catalog dates, Permissions and AllPermissions 3358, and
/// Properties: 8 built-ins and its stored ones (r-dao-1b/documents-members-each: MSysDb lists 14).
/// </summary>
internal sealed unsafe class DocumentObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Document;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, d => Text(d._entry.Name)))
        .Get("Owner", (self, args) => Live(self, d => Text("")))
        .Get("UserName", (self, args) => Live(self, d => Text(d.Database.Workspace.UserName)))
        .Get("Container", (self, args) => Live(self, d => Text(d._container.Name)))
        .Get("DateCreated", (self, args) => Live(self, d => CatalogDates.Date(d._entry.DateCreateBits)))
        .Get("LastUpdated", (self, args) => Live(self, d => CatalogDates.Date(d._entry.DateUpdateBits)))
        .Get("Permissions", (self, args) => Live(self, d => d.Fail(3358, DaoErrors.NoWorkgroupFile)))
        .Get("AllPermissions", (self, args) => Live(self, d => d.Fail(3358, DaoErrors.NoWorkgroupFile)))
        .Put("Permissions", (self, args) => Live(self, d => ContainerObject.NamedSecurityRefusal()))
        .Get("Properties", (self, args) => Live(self, d => d.PropertiesCollection()));

    private readonly DefinitionCollection _owner;
    private readonly ContainerObject _container;
    private readonly CatalogEntry _entry;
    private PropertiesObject? _properties;

    public DocumentObject(DefinitionCollection owner, ContainerObject container, CatalogEntry entry) : base(Decl, Vtable, owner.Engine, "DAO.Document")
    {
        _owner = owner;
        _container = container;
        _entry = entry;
        owner.AddRef();
        container.AddRef();
    }

    private DatabaseObject Database => _owner.Database;

    public override bool Invalid => Database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<DocumentObject, CallResult> op)
    {
        var d = (DocumentObject)self;
        return d.Invalid ? d.Fail(3420, DaoErrors.ObjectInvalid) : op(d);
    }

    private CallResult PropertiesCollection()
    {
        PropertyMap? stored = Database.StoredProperties(_entry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Document.Properties when the property blob cannot be read: " + failure));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Document, stored));
        return Object(_properties);
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _container.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Relation (R-CAT-1 CAT-D; r-dao-1b/relation-members-each): Name, Table, ForeignTable, Attributes from
/// MSysRelationships, PartialReplica 3032, Fields (its column pairs) and 5 built-in Properties.
/// </summary>
internal sealed unsafe class RelationObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Relation;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, r => Text(r._relationship.Name)))
        .Get("Table", (self, args) => Live(self, r => Text(r._relationship.Table)))
        .Get("ForeignTable", (self, args) => Live(self, r => Text(r._relationship.ForeignTable)))
        .Get("Attributes", (self, args) => Live(self, r => Int32(r._relationship.Attributes)))
        .Get("PartialReplica", (self, args) => Live(self, r => r.Fail(3032, DaoErrors.CannotPerform)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection())))
        .Get("Properties", (self, args) => Live(self, r => Object(r._properties ??= new PropertiesObject(r, BuiltInProperties.Relation))));

    private readonly DefinitionCollection _owner;
    private readonly Relationship _relationship;
    private DefinitionCollection? _fields;
    private PropertiesObject? _properties;

    public RelationObject(DefinitionCollection owner, Relationship relationship) : base(Decl, Vtable, owner.Engine, "DAO.Relation")
    {
        _owner = owner;
        _relationship = relationship;
        owner.AddRef();
    }

    public override bool Invalid => _owner.Database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<RelationObject, CallResult> op)
    {
        var r = (RelationObject)self;
        return r.Invalid ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    private DefinitionCollection FieldsCollection() =>
        _fields ??= new DefinitionCollection(DaoTypes.Fields, "DAO.Fields", _owner.Database, [.. _relationship.Columns.Select(c => c.Referenced)],
            (c, i) => new RelationFieldObject(c, _relationship.Columns[i], i));

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _properties?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Field of a Relation (r-dao-1b/relationfield-members-each): Name the primary column, ForeignName the foreign one,
/// CollectionIndex its ordinal; FieldSize 3267, OriginalValue and VisibleValue 3251, Properties with Count 24 (its
/// entries named), and every other Field member 3219.
/// </summary>
internal sealed unsafe class RelationFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = Build();

    private readonly DefinitionCollection _owner;
    private readonly RelationshipColumn _column;
    private readonly int _ordinal;
    private PropertiesObject? _properties;

    public RelationFieldObject(DefinitionCollection owner, RelationshipColumn column, int ordinal) : base(Decl, Vtable, owner.Engine, "DAO.Field")
    {
        _owner = owner;
        _column = column;
        _ordinal = ordinal;
        owner.AddRef();
    }

    public override bool Invalid => _owner.Database.Closed;

    protected override OperationTable Table => Operations;

    private static OperationTable Build()
    {
        var table = new OperationTable(Decl)
            .Get("Name", (self, args) => Live(self, f => Text(f._column.Referenced)))
            .Get("ForeignName", (self, args) => Live(self, f => Text(f._column.Foreign)))
            .Get("CollectionIndex", (self, args) => Live(self, f => Int16(f._ordinal)))
            .Get("FieldSize", (self, args) => Live(self, f => f.Fail(3267, DaoErrors.RecordsetFieldOnly)))
            .Get("OriginalValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
            .Get("VisibleValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
            // Field.Value failures publish an IErrorInfo (R-ERR-1 ERR-EXCEP; relationfield-members-each).
            .Get("Value", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation, publishesErrorInfo: true)))
            // Count 24 as recorded (relationfield-members-each); the entries are not recorded and are named.
            .Get("Properties", (self, args) => Live(self, f => Object(f._properties ??= new PropertiesObject(f, 24, "the Properties entries of a relation field (relationfield-members-each records only Count 24)"))));
        foreach (string name in (string[])["AllowZeroLength", "Attributes", "CollatingOrder", "ComplexType", "DataUpdatable", "DefaultValue", "Expression",
                     "OrdinalPosition", "Required", "Size", "SourceField", "SourceTable", "Type", "ValidateOnSet", "ValidationRule", "ValidationText"])
        {
            table.Get(name, (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)));
        }
        return table;
    }

    private static CallResult Live(ComObject self, Func<RelationFieldObject, CallResult> op)
    {
        var f = (RelationFieldObject)self;
        return f.Invalid ? f.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}
