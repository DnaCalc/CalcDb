using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;

namespace CalcDb.Com.Dao;

/// <summary>
/// A read-only definition collection (TableDefs, a TableDef's Fields and Indexes, an Index's Fields; R-DAO-1 DAO-COLL):
/// Count, Item by name or ordinal, Refresh. A member wrapper is shared while a client holds it (DAO-ID) and made anew
/// after its last release. <see cref="Denied"/>, when set, is the failure every member gives (ACE refuses the Indexes of
/// a system table to the default Admin with 3110).
/// </summary>
internal sealed unsafe class DefinitionCollection : DaoObject
{
    private static readonly Dictionary<InterfaceDecl, (nint Vtable, OperationTable Operations)> Shapes = new(ReferenceEqualityComparer.Instance);

    private readonly (nint Vtable, OperationTable Operations) _shape;
    private readonly DatabaseObject _database;
    private IReadOnlyList<string> _names;
    private CollectionNames _lookup;
    private Func<DefinitionCollection, int, DaoObject> _make;
    private readonly Dictionary<int, DaoObject> _held = [];

    /// <summary>
    /// The membership as the Database lists it now, for a collection whose members a catalog change alters
    /// (TableDefs, Relations); null for a fixed collection. When a catalog read cannot succeed, returns a failure
    /// reason and leaves the existing members unchanged. The Database relists its one TableDefs object after each
    /// catalog change, so a client that holds it, or fetches it again, sees the new table
    /// (r-q15-ddl-open/lv-rewrite-byte-n: db.TableDefs after the Append is the same object, u6, and Item("T") finds
    /// the new table; r-cat-1/names: Refresh after the Appends, Count 11). Relations stays as last read across SQL
    /// DDL until Refresh reloads it (r-def-cat-txn/cat-drop-table-in-relation #44-45, #140, #188-189;
    /// r-fix-cat-txn/cat-drop-table-in-relation #46-48, #159, #205-207; first fetch after DDL:
    /// r-ddl-limits/accdb-fk-alter #69-70).
    /// </summary>
    public Func<(IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure)>? Membership { get; init; }

    /// <summary>
    /// When true, <see cref="Relist"/> re-binds currently held member wrappers by their name instead of clearing them
    /// (r-dao-1-err-1/draft-querydef: QueryDefs.Refresh keeps identity of held QueryDef wrappers).
    /// </summary>
    public bool PreserveHeldByName { get; init; }

    /// <summary>List the members again from <see cref="Membership"/>; held member wrappers stay valid for their clients.</summary>
    public string? Relist()
    {
        if (Membership is { } membership)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, string? failure) = membership();
            if (failure is not null)
            {
                return failure;
            }
            Dictionary<string, DaoObject>? preserved = null;
            if (PreserveHeldByName && _held.Count > 0)
            {
                preserved = new Dictionary<string, DaoObject>(_held.Count, StringComparer.OrdinalIgnoreCase);
                foreach ((int oldIndex, DaoObject wrapper) in _held)
                {
                    if ((uint)oldIndex < (uint)_names.Count)
                    {
                        preserved.TryAdd(_names[oldIndex], wrapper);
                    }
                }
            }
            (_names, _lookup, _make) = (names, new CollectionNames(names), make);
            _held.Clear(); // the ordinals change
            if (preserved is not null)
            {
                for (int i = 0; i < _names.Count; i++)
                {
                    if (preserved.TryGetValue(_names[i], out DaoObject? wrapper))
                    {
                        _held[i] = wrapper;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Register an already-created live wrapper under its member <paramref name="name"/> so subsequent
    /// <c>Item</c> lookups return that exact COM identity (r-cat-1/temporary-querydef, r-dao-1-err-1/draft-querydef).
    /// </summary>
    public void RegisterHeld(string name, DaoObject wrapper)
    {
        if (_lookup.Find(name) is int ordinal)
        {
            _held[ordinal] = wrapper;
        }
    }

    /// <summary>Return the currently held member wrapper for <paramref name="name"/>, or null if none is held.</summary>
    public DaoObject? FindHeld(string name) =>
        _lookup.Find(name) is int ordinal && _held.TryGetValue(ordinal, out DaoObject? held) ? held : null;

    /// <summary>Snapshot all currently held members in ordinal order so an owner can rebind or invalidate them.</summary>
    public IReadOnlyList<(string Name, DaoObject Wrapper)> SnapshotHeld()
    {
        if (_held.Count == 0)
        {
            return [];
        }
        List<(string Name, DaoObject Wrapper)> result = new(_held.Count);
        foreach ((int ordinal, DaoObject wrapper) in _held.OrderBy(kv => kv.Key))
        {
            if ((uint)ordinal < (uint)_names.Count)
            {
                result.Add((_names[ordinal], wrapper));
            }
        }
        return result;
    }

    /// <summary>
    /// Replace this collection's membership in place while keeping the collection's COM identity
    /// (<c>r-cat-1/held-sql-replace</c>, <c>r-dao-1-err-1/query-sql-rewrite</c>: <c>QueryDef.Parameters</c> stays
    /// the same COM object across <c>qd.SQL = ...</c>, preserving surviving held <c>Parameter</c> wrappers).
    /// </summary>
    public void Rebind(
        IReadOnlyList<string> names,
        Func<DefinitionCollection, int, DaoObject> make,
        IReadOnlyDictionary<int, DaoObject>? preservedHeld = null,
        string? gap = null,
        (int Number, string Text)? countDenied = null,
        string? itemGap = null)
    {
        _names = names;
        _lookup = new CollectionNames(names);
        _make = make;
        Gap = gap;
        CountDenied = countDenied;
        ItemGap = itemGap;
        _held.Clear();
        if (preservedHeld is not null)
        {
            foreach ((int ordinal, DaoObject wrapper) in preservedHeld)
            {
                if ((uint)ordinal < (uint)_names.Count)
                {
                    _held[ordinal] = wrapper;
                }
            }
        }
    }

    /// <param name="holdsDatabase">False for the Database's own TableDefs, which the Database keeps (a reference back would be a cycle).</param>
    public DefinitionCollection(InterfaceDecl decl, string source, DatabaseObject database, IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, bool holdsDatabase = true)
        : this(decl, Shape(decl), source, database, names, make, holdsDatabase)
    {
    }

    private DefinitionCollection(InterfaceDecl decl, (nint Vtable, OperationTable Operations) shape, string source, DatabaseObject database, IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, bool holdsDatabase)
        : base(decl, shape.Vtable, database.Engine, source)
    {
        _shape = shape;
        _database = database;
        _names = names;
        _lookup = new CollectionNames(names);
        _make = make;
        _holdsDatabase = holdsDatabase;
        if (holdsDatabase)
        {
            database.AddRef();
        }
    }

    private readonly bool _holdsDatabase;

    /// <summary>The collection's Append (TableDefs.Append, QueryDefs.Append); null where the slice has none.</summary>
    public Func<DefinitionCollection, nint, CallResult>? Appender { get; init; }

    /// <summary>The collection's Delete (QueryDefs.Delete, W-07/saved-definition); null where the slice has none.</summary>
    public Func<DefinitionCollection, nint, CallResult>? Deleter { get; init; }

    /// <summary>When set and returning true, the collection's owner (e.g. a deleted QueryDef) is invalid -> 3420.</summary>
    public Func<bool>? IsOwnerInvalid { get; init; }

    /// <summary>The failure every member gives instead of an answer (number and text), or null.</summary>
    public (int Number, string Text)? Denied { get; init; }

    /// <summary>A named gap: when set, Count and Item give it as not implemented (a membership CalcDb cannot yet decide).</summary>
    public string? Gap { get; set; }

    /// <summary>
    /// The source of the 3420 a member gives after its Database closed, when it is the owner's rather than the
    /// collection's (r-dao-1-err-1/life-database-workspace-close: TableDefs.Count and Containers.Count from DAO.Database,
    /// Indexes.Count from DAO.TableDef, Parameters.Count from DAO.QueryDef); null for the collection's own.
    /// </summary>
    public string? ClosedSource { get; init; }

    /// <summary>
    /// The source of the 3265 for Item("") when it is the owner's (r-dao-1-err-1/coll-name-variants: a TableDef's
    /// Fields.Item("") from DAO.TableDef, every other missing name from DAO.Fields); null for the collection's own.
    /// </summary>
    public string? EmptyNameSource { get; init; }

    /// <summary>
    /// Item with an object index as ACE answers it for this collection (r-dao-1-err-1/coll-tabledefs-index-variants:
    /// TableDefs.Item(Database) 3421 from DAO.TableDefs, Errors 3001 then 3421), or null where no record shows it (named).
    /// </summary>
    public (int Number, string Text)? ObjectIndexFailure { get; init; }

    /// <summary>
    /// Whether Item cuts a name at its first NUL (r-dao-1-err-1/coll-name-variants: TableDefs.Item("T\0") is T, where
    /// that record's Fields lookups keep the NUL).
    /// </summary>
    public bool NameEndsAtNul { get; init; }

    /// <summary>ACE's failure of Count alone (Parameters.Count 3060, r-sql-1/b-parameters), or null.</summary>
    public (int Number, string Text)? CountDenied { get; set; }

    /// <summary>A named gap of Item alone, when Count answers (or fails as ACE does) and Item is not recorded.</summary>
    public string? ItemGap { get; set; }

    public override bool Invalid => _database.Closed || (IsOwnerInvalid?.Invoke() ?? false);

    public DatabaseObject Database => _database;

    protected override OperationTable Table => _shape.Operations;

    private static (nint, OperationTable) Shape(InterfaceDecl decl)
    {
        lock (Shapes)
        {
            if (!Shapes.TryGetValue(decl, out (nint, OperationTable) shape))
            {
                OperationTable operations = new OperationTable(decl)
                    .Get("Count", (self, args) => Live(self, c => c.Gap is { } gap ? CallResult.Fail(Failure.NotImplemented(gap))
                        : c.CountDenied is { } denied ? c.Fail(denied.Number, denied.Text) : Int16(c._names.Count)))
                    .Get("Item", (self, args) => { nint index = args[0]; return Live(self, c => (c.Gap ?? c.ItemGap) is { } gap ? CallResult.Fail(Failure.NotImplemented(gap))
                        : c.Item(index)); })
                    .Method("Refresh", (self, args) => Live(self, c => c.Refresh()));
                if (decl.MembersNamed("Append").Length > 0)
                {
                    operations = operations.Method("Append", (self, args) => { nint item = args[0]; return Live(self, c => c.Appender is { } append ? append(c, item) : CallResult.Fail(Failure.NotImplemented(c.Source + ".Append"))); });
                }
                if (decl.MembersNamed("Delete").Length > 0)
                {
                    operations = operations.Method("Delete", (self, args) => { nint name = args[0]; return Live(self, c => c.Deleter is { } delete ? delete(c, name) : CallResult.Fail(Failure.NotImplemented(c.Source + ".Delete"))); });
                }
                shape = (DualEntries.Build(decl), operations);
                Shapes[decl] = shape;
            }
            return shape;
        }
    }

    private static CallResult Live(ComObject self, Func<DefinitionCollection, CallResult> op)
    {
        var c = (DefinitionCollection)self;
        if (c.Invalid)
        {
            string source = !c._database.Closed && c.OwnerInvalidSource is { } owner ? owner : c.ClosedSource ?? c.Source;
            return CallResult.Fail(Failure.Dao(3420, source, DaoErrors.ObjectInvalid));
        }
        return c.Denied is { } denied ? c.Fail(denied.Number, denied.Text) : op(c);
    }

    /// <summary>The explicit Refresh: list the members again, then tell the owner which wrappers were held before (<see cref="Refreshed"/>).</summary>
    private CallResult Refresh()
    {
        IReadOnlyList<(string Name, DaoObject Wrapper)> held = Refreshed is null ? [] : SnapshotHeld();
        if (Relist() is { } fail)
        {
            return CallResult.Fail(Failure.NotImplemented(fail));
        }
        Refreshed?.Invoke(this, held);
        return CallResult.Nothing;
    }

    /// <summary>
    /// Called after an explicit Refresh with the member wrappers held before it (TableDefs: a TableDef whose table another
    /// binding dropped becomes invalid, r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*); null for the other collections.
    /// </summary>
    public Action<DefinitionCollection, IReadOnlyList<(string Name, DaoObject Wrapper)>>? Refreshed { get; init; }

    /// <summary>
    /// The source of the 3420 a member gives when <see cref="IsOwnerInvalid"/> holds while the Database is open, when it is
    /// the owner's (a dropped TableDef's Fields.Count from DAO.TableDef, r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*
    /// call 125); null for <see cref="ClosedSource"/> or the collection's own.
    /// </summary>
    public string? OwnerInvalidSource { get; set; }

    private CallResult Item(nint index)
    {
        int? ordinal = CollectionLookup.Find(index, _lookup, out bool objectIndex);
        if (objectIndex)
        {
            return ObjectIndexFailure is { } refused
                ? CallResult.Fail(Failure.Dao(refused.Number, Source, refused.Text) with { Preceding = Failure.Dao(3001, Source, DaoErrors.InvalidArgument) })
                : CallResult.Fail(Failure.NotImplemented("a definition collection's Item with an object index"));
        }
        if (ordinal is null && NameEndsAtNul && index != 0 && Args.TypeOf(index) == Foundation.VarType.Bstr && Args.Text(index) is { } text && text.IndexOf('\0') is int nul and >= 0)
        {
            ordinal = _lookup.Find(text.AsSpan(0, nul));
        }
        if (ordinal is not int i)
        {
            return EmptyNameSource is { } owner && Args.TypeOf(index) == Foundation.VarType.Bstr && Args.Text(index) is ""
                ? CallResult.Fail(Failure.Dao(3265, owner, DaoErrors.ItemNotFound))
                : Fail(3265, DaoErrors.ItemNotFound);
        }
        if (_held.TryGetValue(i, out DaoObject? held))
        {
            return Object(held);
        }
        DaoObject made = _make(this, i);
        _held[i] = made;
        CallResult result = Object(made);
        made.Release();
        return result;
    }

    /// <summary>A member's last release: the next Item makes a new wrapper.</summary>
    public void Released(DaoObject member)
    {
        foreach (KeyValuePair<int, DaoObject> kv in _held.Where(kv => ReferenceEquals(kv.Value, member)).ToArray())
        {
            _held.Remove(kv.Key);
        }
    }

    protected override void OnFinalRelease()
    {
        if (_holdsDatabase)
        {
            _database.Release();
        }
    }
}

/// <summary>
/// A TableDef of a read-only Database (II-06; W-08/native-read): the catalog row (name, flags, dates) and the decoded
/// TableDef (row count, columns, indexes). A system table shows no Fields and refuses its Indexes to the default Admin
/// (r-cat-1b/tabledef-members-ddl: Fields.Count 0, Indexes.Count 3110). Attributes is the catalog Flags value as ACE
/// reports it for these tables (dbSystemObject 0x80000000 for MSys tables, 0 for the user tables observed).
/// </summary>
internal sealed unsafe class TableDefObject : DaoObject
{
    internal const int SystemObjectFlag = unchecked((int)0x80000000);

    private static readonly InterfaceDecl Decl = DaoTypes.TableDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, t => Text(t._entry.Name)))
        .Get("Attributes", (self, args) => Live(self, t => Int32(t._entry.Flags)))
        .Get("Connect", (self, args) => Live(self, t => Text("")))
        .Get("SourceTableName", (self, args) => Live(self, t => Text("")))
        .Get("Updatable", (self, args) => Live(self, t => t._owner.Database.InheritedWritable
            ? DatabaseObject.InheritedWriteRefusal("TableDef.Updatable")
            : Bool(!t._owner.Database.ReadOnly)))
        .Get("DateCreated", (self, args) => Live(self, t => t.DroppedByPeer ? DroppedDate : CatalogDates.Date(t._entry.DateCreateBits)))
        .Get("LastUpdated", (self, args) => Live(self, t => t.DroppedByPeer ? DroppedDate : CatalogDates.Date(t._entry.DateUpdateBits)))
        .Get("RecordCount", (self, args) => Live(self, t => t.Definition is { } d ? Int32((int)d.RowCount) : t.DefinitionFailure()))
        .Get("ValidationRule", (self, args) => Live(self, t => t.StoredText("ValidationRule")))
        .Put("ValidationRule", (self, args) => { nint value = args[^1]; return Live(self, t => t.PutValidationRule(value)); })
        .Get("ValidationText", (self, args) => Live(self, t => t.StoredText("ValidationText")))
        .Get("Properties", (self, args) => Live(self, t => t.PropertiesCollection()))
        .Get("ConflictTable", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("ReplicaFilter", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("Fields", (self, args) => Live(self, t => t.FieldsCollection()))
        .Get("Indexes", (self, args) => Live(self, t => t.IndexesCollection()))
        .Method("CreateField", (self, args) => { nint name = args.Length > 0 ? args[0] : 0, type = args.Length > 1 ? args[1] : 0, size = args.Length > 2 ? args[2] : 0; return Live(self, t => t.CreateField(name, type, size)); })
        .Method("CreateIndex", (self, args) => { nint name = args.Length > 0 ? args[0] : 0; return Live(self, t => NewIndexObject.Create(t.Engine, name)); })
        .Method("OpenRecordset", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, options = args.Length > 1 ? args[1] : 0; return Live(self, t => t.OpenRecordset(type, options)); });

    private readonly DefinitionCollection _owner;
    private CatalogEntry _entry;
    private DefinitionCollection? _fields;
    private DefinitionCollection? _indexes;
    private PropertiesObject? _properties;

    public TableDefObject(DefinitionCollection owner, CatalogEntry entry) : base(Decl, Vtable, owner.Engine, "DAO.TableDef")
    {
        _owner = owner;
        _entry = entry;
        owner.AddRef();
        owner.Database.TrackTableDef(this);
        Definition = owner.Database.Definition(entry, out _definitionFailure);
        Blob = owner.Database.Blob(entry, out _blobFailure);
    }

    private string? _definitionFailure;
    private string? _blobFailure;

    /// <summary>The table's MR2 property blob (CAT-P): its own map and one map per column with stored properties.</summary>
    public PropertyBlob? Blob { get; private set; }

    public string Name => _entry.Name;

    /// <summary>The catalog row this TableDef was made from (or last refreshed to).</summary>
    internal CatalogEntry Entry => _entry;

    public override bool Invalid => _dropped || _owner.Database.Closed;

    private bool _dropped;

    /// <summary>
    /// Whether another binding of the file dropped this table and the owning TableDefs has not been refreshed since
    /// (<c>evidence/r-q47-bigint-expr-edges/mh-held-tdf-after-drop-*</c> calls 39–101): the cached metadata still answers
    /// (Name, RecordCount, Attributes, Fields.Count, a field's Name and Type, Indexes.Count), DateCreated and LastUpdated
    /// read <c>VT_DATE 0</c>, and OpenRecordset and Fields.Append fail 3078.
    /// </summary>
    internal bool DroppedByPeer => _owner.Database.DroppedByPeer(_entry);

    /// <summary>DateCreated and LastUpdated of a TableDef whose table another binding dropped: VT_DATE 0 (mh-held-tdf-after-drop-* calls 51, 55).</summary>
    private static CallResult DroppedDate => CallResult.OfScalar(Foundation.VarType.Date, 0L);

    /// <summary>
    /// The owning TableDefs was refreshed after another binding dropped this table: every member of this TableDef, its
    /// Fields and Indexes collections and its fields fail 3420 from now on (mh-held-tdf-after-drop-* calls 113–143).
    /// </summary>
    internal void InvalidateDropped() => _dropped = true;

    public TableDefinition? Definition { get; private set; }

    internal bool IsSystem => (_entry.Flags & SystemObjectFlag) != 0;

    protected override OperationTable Table => Operations;

    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, Foundation.VarType sourceType) =>
        member.Kind == InvokeKind.PropertyPut && member.Name == "ValidationRule" && sourceType != Foundation.VarType.Bstr
            ? Failure.NotImplemented($"TableDef.ValidationRule put of variant type {sourceType} (not recorded)")
            : null;

    /// <summary>
    /// Reload this TableDef's catalog row, TableDef page, and MR2 property blob after a property put or catalog DDL
    /// published a new catalog image, keeping held <see cref="DefinitionFieldObject"/> wrappers live against the
    /// updated blob (W-08, r-q37; W-07, r-q46).
    /// </summary>
    internal void Refresh()
    {
        CatalogEntry? updated = _owner.Database.Catalog.LocalTables(_owner.Database.Format)
            .FirstOrDefault(e => e.Id == _entry.Id);
        if (updated is not null)
        {
            _entry = updated;
        }
        Definition = _owner.Database.Definition(_entry, out _definitionFailure);
        Blob = _owner.Database.Blob(_entry, out _blobFailure);
        _properties?.Release();
        _properties = null;
    }

    private static CallResult Live(ComObject self, Func<TableDefObject, CallResult> op)
    {
        var t = (TableDefObject)self;
        return t.Invalid ? t.Fail(3420, DaoErrors.ObjectInvalid) : op(t);
    }

    private CallResult DefinitionFailure() => CallResult.Fail(Failure.NotImplemented("TableDef member when the TableDef cannot be read: " + _definitionFailure));

    private CallResult BlobFailure() => CallResult.Fail(Failure.NotImplemented("TableDef member when the property blob cannot be read: " + _blobFailure));

    /// <summary>ValidationRule and ValidationText: the stored value of the table's own map (CAT-P), "" when none is stored.</summary>
    private CallResult StoredText(string name)
    {
        if (_blobFailure is not null)
        {
            return BlobFailure();
        }
        StoredProperty? stored = Blob?.ObjectMap?.Properties.FirstOrDefault(p => p.Name == name);
        return stored is null ? Text("") : PropertyObject.StoredValue(stored);
    }

    private CallResult PutValidationRule(nint valueArg)
    {
        if (_owner.Database.ReadOnly)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.ValidationRule put on a read-only Database (not recorded)"));
        }
        if (_owner.Database.InheritedWritable)
        {
            return DatabaseObject.InheritedWriteRefusal("TableDef.ValidationRule put");
        }
        if (IsSystem)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.ValidationRule put on a system table (not recorded)"));
        }
        if (_owner.Database.Workspace.Transactions.Depth > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.ValidationRule put inside BeginTrans (not recorded)"));
        }
        if (Definition is not { } definition || definition.RowCount > 0)
        {
            return CallResult.Fail(Failure.NotImplemented("TableDef.ValidationRule put on a populated or unreadable table (not recorded)"));
        }
        Foundation.VarType vt = Args.TypeOf(valueArg);
        if (vt != Foundation.VarType.Bstr)
        {
            return CallResult.Fail(Failure.NotImplemented($"TableDef.ValidationRule put of variant type {vt} (not recorded)"));
        }
        string text = Args.Text(valueArg) ?? "";
        return CallResult.Fail(FieldValidationRuleValidator.ValidateTableRuleRefusal(text));
    }

    /// <summary>The 12 built-ins, then the table's own stored properties (r-dao-1b/tabledef-properties-enumerated: Description).</summary>
    private CallResult PropertiesCollection()
    {
        if (_blobFailure is not null)
        {
            return BlobFailure();
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.TableDef, Blob?.ObjectMap));
        return Object(_properties);
    }

    private CallResult CreateField(nint name, nint type, nint size) =>
        NewFieldObject.Create(Engine, forAttachedTable: true, isJet4: _owner.Database.Profile.Format == FileFormat.Jet4, name, type, size);

    private CallResult FieldsCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        if (_fields is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, _) = FieldsMembership(reload: false);
            _fields = new DefinitionCollection(DaoTypes.Fields, "DAO.Fields", _owner.Database, names, make)
            {
                EmptyNameSource = "DAO.TableDef", // r-dao-1-err-1/coll-name-variants: Item("") of a TableDef's Fields
                Membership = () => FieldsMembership(reload: true),
                PreserveHeldByName = true,
                Appender = (c, arg) => c.Database.AppendField(this, c, arg),
                Deleter = (c, nameArg) => c.Database.DeleteField(this, c, nameArg),
                IsOwnerInvalid = () => _dropped,
                OwnerInvalidSource = "DAO.TableDef", // r-q47-bigint-expr-edges/mh-held-tdf-after-drop-* call 125
            };
        }
        return Object(_fields);
    }

    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) FieldsMembership(bool reload)
    {
        if (reload)
        {
            Refresh();
        }
        if (Definition is not { } d)
        {
            return ([], static (_, _) => throw new InvalidOperationException(), "TableDef cannot be read: " + _definitionFailure);
        }
        IReadOnlyList<ColumnDescriptor> columns = IsSystem ? [] : d.Columns;
        return ([.. columns.Select(c => c.Name)], (c, i) => new DefinitionFieldObject(c, this, columns[i], null, i), null);
    }

    private CallResult IndexesCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        if (_indexes is null)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make, string? fail) = IndexesMembership(reload: false);
            if (fail is not null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.IndexNameOrder", fail));
            }
            _indexes = new DefinitionCollection(DaoTypes.Indexes, "DAO.Indexes", _owner.Database, names, make)
            {
                Denied = IsSystem ? (3110, DaoErrors.NoReadDefinitions(_entry.Name)) : null,
                ClosedSource = "DAO.TableDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
                Membership = () => IndexesMembership(reload: true),
                PreserveHeldByName = true,
                Appender = (c, arg) => c.Database.AppendIndex(this, c, arg),
                Deleter = (c, nameArg) => c.Database.DeleteIndex(this, c, nameArg),
                IsOwnerInvalid = () => _dropped, // r-q47-bigint-expr-edges/mh-held-tdf-after-drop-* call 137: 3420 from DAO.TableDef
            };
        }
        return Object(_indexes);
    }

    private (IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make, string? Failure) IndexesMembership(bool reload)
    {
        if (reload)
        {
            Refresh();
        }
        if (Definition is not { } d)
        {
            return ([], static (_, _) => throw new InvalidOperationException(), "TableDef cannot be read: " + _definitionFailure);
        }
        LogicalIndex[] listed = [];
        if (!IsSystem)
        {
            LogicalIndex[]? sorted = _owner.Database.Collation is { } order
                ? ListedIndexes.Of(d.LogicalIndexes, _owner.Database.Format.IndexDescriptor, order)
                : ListedIndexes.OfPlainNames(d.LogicalIndexes, _owner.Database.Format.IndexDescriptor);
            if (sorted is null)
            {
                return ([], static (_, _) => throw new InvalidOperationException(), $"the Indexes of '{_entry.Name}' in a sort order outside the General family, with names whose order depends on it (FMT1-INDEX-ENUM was asked under General)");
            }
            listed = sorted;
        }
        return ([.. listed.Select(i => i.Name)], (c, i) => new IndexObject(c, d, listed[i]), null);
    }

    /// <summary>
    /// <c>_TableDef.OpenRecordset(Type, Options)</c> (dispid 1610809362; W-08 / R-DAO-1 / R-FMT-1): opens a snapshot
    /// (<c>VT_I4 4</c>, omitted <c>Options</c>) over a live local table via <see cref="DatabaseObject.OpenLocalTableRecordset"/>
    /// (<c>r-fmt-1/read-accdb-*</c>, <c>r-fmt-1/read-mdb-*</c>, <c>r-wire/ident-tabledef-vs-recordset</c> call 15,
    /// <c>r-fix-fmt-sort/fmt1-*</c>; system tables fail 3112 from <c>DAO.Database</c>). Deleted/stale TableDefs,
    /// omitted or non-<c>dbOpenSnapshot</c> <c>Type</c>, non-numeric <c>Type</c>, and explicit <c>Options</c> have no
    /// retained ACE record and stay refused by name.
    /// </summary>
    private CallResult OpenRecordset(nint typeArg, nint optionsArg)
    {
        CatalogEntry? liveEntry = _owner.Database.Catalog.LocalTables(_owner.Database.Format)
            .FirstOrDefault(e => e.Id == _entry.Id && string.Equals(e.Name, _entry.Name, StringComparison.OrdinalIgnoreCase));
        if (liveEntry is null)
        {
            // Another binding of the file dropped the table: dbOpenSnapshot (VT_I4 4) with Options omitted fails 3078 from
            // DAO.Database (r-q47-bigint-expr-edges/mh-held-tdf-after-drop-accdb calls 75, 102, 202).
            if (DroppedByPeer && optionsArg == 0 && typeArg != 0 && Args.TypeOf(typeArg) == Foundation.VarType.I4 && (int)Args.Cell(typeArg)->Int64 == 4)
            {
                return CallResult.Fail(Failure.Dao(3078, "DAO.Database", global::CalcDb.Sql.SqlMessages.NoInputTable(_entry.Name)));
            }
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.TableDefOpenRecordsetStale",
                $"TableDef.OpenRecordset on deleted or stale TableDef '{_entry.Name}' (3265/3420/3011 is not recorded)"));
        }
        if (optionsArg != 0 && !TypedCall.IsMissing(Args.Cell(optionsArg)))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.TableDefOpenRecordsetOptions",
                $"TableDef.OpenRecordset with explicit Options ({Args.TypeOf(optionsArg)}) is not recorded (only omitted Options is recorded)"));
        }
        if (typeArg == 0 || TypedCall.IsMissing(Args.Cell(typeArg)))
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.TableDefOpenRecordsetType",
                "TableDef.OpenRecordset with omitted Type is not recorded (only VT_I4 dbOpenSnapshot (4) is recorded)"));
        }
        Foundation.VarType typeVt = Args.TypeOf(typeArg);
        if (typeVt != Foundation.VarType.I4)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.TableDefOpenRecordsetArguments",
                $"TableDef.OpenRecordset with Type of variant type {typeVt} is not recorded (only VT_I4 dbOpenSnapshot (4) is recorded)"));
        }
        int recordsetType = (int)Args.Cell(typeArg)->Int64;
        if (recordsetType != 4)
        {
            return CallResult.Fail(Failure.NotModelled(
                "NotModelled.TableDefOpenRecordsetType",
                $"TableDef.OpenRecordset with Type {recordsetType} is not recorded (only dbOpenSnapshot (4) is recorded)"));
        }
        return _owner.Database.OpenLocalTableRecordset(liveEntry, type: 4, options: 0, lockEdit: null, fromTableDef: true);
    }

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _indexes?.Release();
        _properties?.Release();
        _owner.Database.UntrackTableDef(this);
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// The logical indexes a TableDef lists (qualified.md R-FMT-1 FMT1-INDEX-ENUM; task 6fsf): in name order under the
/// database's text order (General: underscore before digits before letters, upper and lower case together:
/// r-q-fmt-sort/index-enum-order-accdb), without the foreign-key entries on a relationship's primary side. Those entries
/// (index type 2, related-table type 1; named ".rF" and ".rB" in r-dao-1b-rich, three characters stored and decoded in
/// full) are never listed: r-dao-1b/index-members-each lists Parent's five indexes without ".rF" and Child's PK_Child and
/// RelMain (type 2, related-table type 2, the secondary side) without ".rB".
/// </summary>
internal static class ListedIndexes
{
    /// <summary>The related-table type of a relationship's primary side (II-05 §1.3: 1 primary, 2 secondary).</summary>
    private const byte PrimarySide = 1;

    public static LogicalIndex[] Of(IReadOnlyList<LogicalIndex> stored, IndexDescriptorRules rules, CalcDb.Values.Scalar.TextCollation order)
    {
        // A stable sort: two names equal in the text order keep their stored order (not recorded).
        return [.. Visible(stored, rules).OrderBy(i => i.Name, Comparer<string>.Create((a, b) => order.Compare(a, b)))];
    }

    /// <summary>
    /// The listed indexes in a sort order CalcDb has no collation for (outside the General family): in name order when no
    /// name pair's order can depend on the sort order, that is, when each adjacent pair first differs at two ASCII letters
    /// or digits, compared case-insensitively (r-sort-3b/lu-unique-survivors, CollatingOrder 2057: IX_K, IX_KD, PK_T and
    /// PK_UQ, UX_K in name order). Null when a pair first differs elsewhere (an underscore, punctuation, a non-ASCII unit).
    /// </summary>
    public static LogicalIndex[]? OfPlainNames(IReadOnlyList<LogicalIndex> stored, IndexDescriptorRules rules)
    {
        LogicalIndex[] listed = [.. Visible(stored, rules).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
        for (int k = 1; k < listed.Length; k++)
        {
            string a = listed[k - 1].Name, b = listed[k].Name;
            int at = 0;
            while (at < a.Length && at < b.Length && char.ToUpperInvariant(a[at]) == char.ToUpperInvariant(b[at]))
            {
                at++;
            }
            // One name a prefix of the other orders the same in every sort order; two equal names keep their stored order.
            if (at < a.Length && at < b.Length && !(char.IsAsciiLetterOrDigit(a[at]) && char.IsAsciiLetterOrDigit(b[at])))
            {
                return null;
            }
        }
        return listed;
    }

    private static IEnumerable<LogicalIndex> Visible(IReadOnlyList<LogicalIndex> stored, IndexDescriptorRules rules) =>
        stored.Where(i => !(i.IndexType == rules.ForeignKeyType && i.RelatedTableType == PrimarySide));
}

/// <summary>
/// An Index of a TableDef (II-05 §1; r-cat-1b/index-members-ddl, evidence/r-q-fmt-sort/dc-rules-accdb,
/// evidence/r-q10-statistics-words): Primary and Foreign from the logical index type, Unique, IgnoreNulls and Required
/// from the physical tree's flags, Clustered False, DistinctCount the physical index's stored UniqueEntryCount word
/// (word1), and its Fields.
/// </summary>
internal sealed unsafe class IndexObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Index;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, x => Text(x._logical.Name)))
        .Get("Primary", (self, args) => Live(self, x => Bool(x._logical.IndexType == x.Rules.PrimaryKeyType)))
        .Put("Primary", (self, args) => Live(self, x => x.Fail(3268, DaoErrors.CannotSetOnceInCollection)))
        .Get("Foreign", (self, args) => Live(self, x => Bool(x._logical.IndexType == x.Rules.ForeignKeyType)))
        .Get("Unique", (self, args) => Live(self, x => Bool(x._physical.Unique(x.Rules))))
        .Put("Unique", (self, args) => Live(self, x => x.Fail(3268, DaoErrors.CannotSetOnceInCollection)))
        .Get("IgnoreNulls", (self, args) => Live(self, x => Bool(x._physical.IgnoreNulls(x.Rules))))
        .Put("IgnoreNulls", (self, args) => Live(self, x => x.Fail(3268, DaoErrors.CannotSetOnceInCollection)))
        .Get("Required", (self, args) => Live(self, x => Bool(x._physical.Required(x.Rules))))
        .Put("Required", (self, args) => Live(self, x => x.Fail(3268, DaoErrors.CannotSetOnceInCollection)))
        .Get("Clustered", (self, args) => Live(self, x => Bool(false)))
        .Get("DistinctCount", (self, args) => Live(self, x => x.DistinctCount()))
        .Get("Fields", (self, args) => Live(self, x => x.FieldsCollection()))
        .Get("Properties", (self, args) => Live(self, x => Object(x._properties ??= new PropertiesObject(x, BuiltInProperties.Index))))
        .Method("CreateField", (self, args) => { nint name = args.Length > 0 ? args[0] : 0; return Live(self, x => NewIndexFieldObject.Create(x.Engine, name)); });

    private readonly DefinitionCollection _owner;
    private readonly TableDefinition _table;
    private readonly LogicalIndex _logical;
    private readonly PhysicalIndex _physical;
    private DefinitionCollection? _fields;
    private PropertiesObject? _properties;
    private bool _deleted;

    public override bool Invalid => _deleted || _owner.Invalid;

    internal void InvalidateDeleted()
    {
        _deleted = true;
        if (_fields is not null)
        {
            _fields.OwnerInvalidSource = "DAO.Index"; // r-q52-ddl-make-domain/dao-idx-errors-accdb, dao-idx-errors-mdb call 469
        }
    }

    public IndexObject(DefinitionCollection owner, TableDefinition table, LogicalIndex logical) : base(Decl, Vtable, owner.Engine, "DAO.Index")
    {
        _owner = owner;
        _table = table;
        _logical = logical;
        _physical = table.PhysicalOf(logical)!;
        owner.AddRef();
    }

    private IndexDescriptorRules Rules => _owner.Database.Format.IndexDescriptor;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<IndexObject, CallResult> op)
    {
        var x = (IndexObject)self;
        return x.Invalid ? x.Fail(3420, DaoErrors.ObjectInvalid) : op(x);
    }

    private CallResult DistinctCount()
    {
        using Storage.PagePin? pin = _owner.Database.Cache.Pin(_table.FirstPage).Pin;
        if (pin is not null && TableDefUpdater.TryReadUniqueEntryCount(pin.Bytes, _physical.Number, _owner.Database.Format, out uint current))
        {
            return Int32((int)current);
        }
        return Int32((int)_physical.UniqueEntryCount);
    }

    private CallResult FieldsCollection()
    {
        ColumnDescriptor[] columns = [.. _physical.Columns.Select(ic => _table.Columns.First(c => c.PhysicalNumber == ic.PhysicalNumber))];
        _fields ??= new DefinitionCollection(DaoTypes.IndexFields, "DAO.Fields", // an index field's Item(99) is 3265 from DAO.Fields (r-dao-1b/coll-item-table-family)
             _owner.Database, [.. columns.Select(c => c.Name)],
            (c, i) => new DefinitionFieldObject(c, null, columns[i], _physical.Columns[i], i))
        {
            Appender = static (c, _) => c.Fail(3219, DaoErrors.InvalidOperation), // r-q52-ddl-make-domain/dao-idx-errors-accdb, dao-idx-errors-mdb call 369
            IsOwnerInvalid = () => Invalid,
        };
        return Object(_fields);
    }

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _properties?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Field of a definition (a TableDef's or an Index's Fields; Field3). A TableDef field (r-dao-1b/field-members-typed-
/// each, field-properties-enumerated; r-cat-1b/field-members-builtin) answers Name, Type, Size, OrdinalPosition and
/// CollectionIndex from the column; its stored column properties (CAT-P: DefaultValue, ValidationRule, ValidationText
/// as "" and Required, AllowZeroLength as False when none is stored); SourceTable and SourceField (its own table and
/// name), AppendOnly False, Expression "", Scale 0 outside Decimal, Attributes by <see cref="ColumnFacts.Attributes"/>
/// and CollatingOrder by <see cref="ColumnFacts.CollatingOrder"/>; Value 3219 with an IErrorInfo, ValidateOnSet,
/// ForeignName and ComplexType 3219, FieldSize 3267, OriginalValue and VisibleValue 3251 (members of a recordset field
/// only). An index field (r-dao-1b/indexfield-members-each) answers only Name, Attributes (dbDescending 1 for a
/// descending column, else 0; r-cat-1b/index-members-ddl) and CollectionIndex: every other member is 3219 (Value with
/// an IErrorInfo), FieldSize 3267, OriginalValue and VisibleValue 3251. Not yet: Precision (1033 for Long and Text in
/// field-properties-enumerated, a value no probe has separated from the collating order), DataUpdatable on a writable
/// open (False read-only) and an index field's Properties.
/// </summary>
internal sealed unsafe class DefinitionFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f._column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.OfTable(t => ColumnFacts.DaoType(f._column) is int type ? Int16(type) : CallResult.Fail(Failure.NotImplemented($"Field.Type of native type {f._column.TypeCode}")))))
        .Get("Size", (self, args) => Live(self, f => f.OfTable(t => ColumnFacts.Size(f._column) is int s ? Int32(s) : CallResult.Fail(Failure.NotImplemented($"Field.Size of native type {f._column.TypeCode}")))))
        .Get("OrdinalPosition", (self, args) => Live(self, f => f.OfTable(t => Int16(f._ordinal))))
        // CollectionIndex of a definition field is 0 at every ordinal, unlike a recordset field's (r-dao-1b/field-members-
        // typed-each: 14 TableDef fields; indexfield-members-each: the second field of a two-field index).
        .Get("CollectionIndex", (self, args) => Live(self, f => Int16(0)))
        // A TableDef field is never dbUpdatableField (r-dao-1b/field-members-typed-each, r-cat-1b/field-members-builtin: 17, 2, 1).
        .Get("Attributes", (self, args) => Live(self, f => f._indexColumn is { } ic ? Int32(ic.Ascending ? 0 : 1)
            : ColumnFacts.Attributes(f._column, updatable: false) is int a ? Int32(a) : CallResult.Fail(Failure.NotModelled("NotModelled.FieldAttributes", $"Field.Attributes of the column {f._column.Name} (calculated, auto-GUID or hyperlink: not recorded)"))))
        .Get("SourceTable", (self, args) => Live(self, f => f.OfTable(t => Text(t.Name))))
        .Get("SourceField", (self, args) => Live(self, f => f.OfTable(t => Text(f._column.Name))))
        .Get("CollatingOrder", (self, args) => Live(self, f => f.OfTable(t => Int32(ColumnFacts.CollatingOrder(f._column)))))
        .Get("DefaultValue", (self, args) => Live(self, f => f.Stored("DefaultValue", Text(""))))
        .Get("ValidationRule", (self, args) => Live(self, f => f.GetValidationRule()))
        .Put("ValidationRule", (self, args) => { nint value = args[^1]; return Live(self, f => f.PutValidationProperty(FieldValidationProperty.ValidationRule, value)); })
        .Get("ValidationText", (self, args) => Live(self, f => f.Stored("ValidationText", Text(""))))
        .Put("ValidationText", (self, args) => { nint value = args[^1]; return Live(self, f => f.PutValidationProperty(FieldValidationProperty.ValidationText, value)); })
        .Get("Required", (self, args) => Live(self, f => f.Stored("Required", Bool(false))))
        .Get("AllowZeroLength", (self, args) => Live(self, f => f.Stored("AllowZeroLength", Bool(false))))
        .Put("AllowZeroLength", (self, args) => { nint value = args[^1]; return Live(self, f => f.PutAllowZeroLength(value)); })
        .Get("AppendOnly", (self, args) => Live(self, f => f.OfTable(t => Bool(false))))
        .Get("Expression", (self, args) => Live(self, f => f.OfTable(t => Text(""))))
        // Field.Precision (evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb,
        // evidence/r-q53-conv-abspos-subq-loc/dao-draft-decimal-accdb, dao-draft-decimal-mdb):
        // VT_I2 declared precision on Decimal (e.g. 18 on DECIMAL(18,4), 38 on DAO dbDecimal), VT_I2 1033 on other types
        // with General (1033) collating order, VT_I2 0 on DAO-created non-Text columns with Neutral (1024) collating order.
        .Get("Precision", (self, args) => Live(self, f => f.OfTable(t => f._column.CalculatedResultType == NativeTypes.Numeric
            ? CallResult.Fail(Failure.NotImplemented("Field.Precision of a calculated Decimal field"))
            : f._column.TypeCode == NativeTypes.Numeric
                ? Int16(f._column.NumericPrecision)
                : ColumnFacts.CollatingOrder(f._column) == 1033
                    ? Int16(1033)
                    : ColumnFacts.CollatingOrder(f._column) == 1024
                        ? Int16(0)
                        : CallResult.Fail(Failure.NotModelled("NotModelled.FieldPrecisionCollatingOrder", $"Field.Precision with CollatingOrder {ColumnFacts.CollatingOrder(f._column)} (only 1024 and 1033 are recorded)")))))
        .Get("Scale", (self, args) => Live(self, f => f.OfTable(t => f._column.CalculatedResultType == NativeTypes.Numeric ? CallResult.Fail(Failure.NotImplemented("Field.Scale of a calculated Decimal field")) : f._column.TypeCode == NativeTypes.Numeric ? Int16(f._column.NumericScale) : Int16(0))))
        .Get("DataUpdatable", (self, args) => Live(self, f => f.OfTable(t => f._owner.Database.ReadOnly ? Bool(false) : CallResult.Fail(Failure.NotImplemented("TableDef Field.DataUpdatable on a writable open (not observed)")))))
        .Get("Value", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation, publishesErrorInfo: true)))
        .Get("ValidateOnSet", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ForeignName", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ComplexType", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("FieldSize", (self, args) => Live(self, f => f.Fail(3267, DaoErrors.RecordsetFieldOnly)))
        .Get("OriginalValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("VisibleValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Properties", (self, args) => Live(self, f => f.PropertiesCollection(f._table)));

    private readonly DefinitionCollection _owner;
    private readonly TableDefObject? _table;
    private readonly ColumnDescriptor _column;
    private readonly IndexColumn? _indexColumn;
    private readonly int _ordinal;
    private PropertiesObject? _properties;
    private bool _validationRuleRead;
    private string? _validationRuleOverride;

    /// <param name="table">The TableDef of a TableDef field; null for an index field, whose further members are not yet built.</param>
    public DefinitionFieldObject(DefinitionCollection owner, TableDefObject? table, ColumnDescriptor column, IndexColumn? indexColumn, int ordinal) : base(Decl, Vtable, owner.Engine, "DAO.Field")
    {
        _owner = owner;
        _table = table;
        _column = column;
        _indexColumn = indexColumn;
        _ordinal = ordinal;
        owner.AddRef();
        table?.AddRef();
    }

    /// <summary>Invalid once its Database closed, or once its TableDef was invalidated after another binding dropped the table (its Fields collection is then invalid).</summary>
    public override bool Invalid => _owner.Invalid;

    protected override OperationTable Table => Operations;

    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, Foundation.VarType sourceType)
    {
        if (member.Kind != InvokeKind.PropertyPut)
        {
            return null;
        }
        if (member.Name == "AllowZeroLength")
        {
            return sourceType != Foundation.VarType.Bool
                ? Failure.NotImplemented($"Field.AllowZeroLength put of variant type {sourceType} (not recorded)")
                : null;
        }
        if (member.Name is not ("ValidationRule" or "ValidationText"))
        {
            return null;
        }
        if (sourceType != Foundation.VarType.Bstr && (Invalid || _table is null))
        {
            return Failure.NotImplemented($"Field.{member.Name} put of variant type {sourceType} on a closed or index Field (qualified.md r-q37 Open)");
        }
        return sourceType is not (Foundation.VarType.Bstr or Foundation.VarType.Null)
            ? Failure.NotImplemented($"Field.{member.Name} put of variant type {sourceType} (qualified.md r-q37 Open)")
            : null;
    }

    protected override Failure? ArgumentConversionFailure(MemberDecl member, ArgumentConversion conversion) =>
        member.Kind == InvokeKind.PropertyPut && member.Name is "ValidationRule" or "ValidationText"
            && !Invalid && _table is not null
            && conversion.Reason == ArgumentConversionReason.TypeMismatch
            && conversion.SourceType == Foundation.VarType.Null
            ? Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true }
            : null;

    private static CallResult Live(ComObject self, Func<DefinitionFieldObject, CallResult> op)
    {
        var f = (DefinitionFieldObject)self;
        return f.Invalid ? f.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    /// <summary>A member of a TableDef field; an index field answers it 3219 without an IErrorInfo (r-dao-1b/indexfield-members-each).</summary>
    private CallResult OfTable(Func<TableDefObject, CallResult> op) =>
        _table is { } t ? op(t) : Fail(3219, DaoErrors.InvalidOperation);

    /// <summary>The column's stored map in the table's blob (CAT-P), by the column's exact name.</summary>
    private PropertyMap? ColumnMap(TableDefObject t) => t.Blob?.Map(PropertySectionKind.ColumnValues, _column.Name);

    private CallResult Stored(string name, CallResult unset) => OfTable(t => ColumnFacts.Stored(t.Blob, _column, name, unset));

    /// <summary>
    /// <c>Field.ValidationRule</c> get: a read on a <see cref="DefinitionFieldObject"/> that has not yet been read returns
    /// the stored NUL-terminated Memo bytes (<c>evidence/r-q37-valrule-put/rule-alone-accdb</c>,
    /// <c>evidence/r-q37-valrule-put/rule-alone-mdb</c>, <c>evidence/r-q37-valrule-put/replace-rule-same-session-accdb</c>,
    /// <c>evidence/r-q42-field-rule-and/put-forms-accdb</c> and <c>put-forms-mdb</c> rows 16-17, 68-69,
    /// <c>evidence/r-q42-field-rule-and/edge-forms-accdb</c> and <c>edge-forms-mdb</c> rows 62-63, 70-71);
    /// once <c>ValidationRule</c> has been read on this COM object, a subsequent non-empty put returns the written text
    /// without a trailing NUL (<c>evidence/r-cat-1/field-rules</c> rows 16, 25-26, 106;
    /// <c>evidence/r-expr-1/mut-validation-rules</c> rows 16, 18, 36, 38;
    /// <c>evidence/r-q42-field-rule-and/put-forms-accdb</c> and <c>put-forms-mdb</c> rows 18-19..88-89), while disk bytes keep the NUL.
    /// </summary>
    private CallResult GetValidationRule() => OfTable(t =>
    {
        _validationRuleRead = true;
        return _validationRuleOverride is not null ? Text(_validationRuleOverride) : ColumnFacts.Stored(t.Blob, _column, "ValidationRule", Text(""));
    });

    /// <summary>
    /// Write <c>Field.ValidationRule</c> or <c>Field.ValidationText</c> on a stored table's field (W-08, tasks 9dvt and
    /// nk97; qualified.md <c>r-q37</c>, <c>evidence/r-q42-field-rule-and/</c>): <c>VT_NULL</c> raises DAO 3421 with
    /// <c>IErrorInfo</c> before touching the database; other non-BSTR variants refuse by name.
    /// </summary>
    private CallResult PutValidationProperty(FieldValidationProperty property, nint valueArg) => OfTable(t =>
    {
        Foundation.VarType vt = Args.TypeOf(valueArg);
        if (vt == Foundation.VarType.Null)
        {
            return Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true);
        }
        if (vt != Foundation.VarType.Bstr)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.{property} put of variant type {vt} (qualified.md r-q37 Open)"));
        }
        string text = Args.Text(valueArg) ?? "";
        CallResult result = _owner.Database.SetFieldValidationProperty(t, this, _column, property, text);
        if (result.Failure is null && property == FieldValidationProperty.ValidationRule)
        {
            _validationRuleOverride = text.Length == 0 || !_validationRuleRead ? null : text;
        }
        return result;
    });

    /// <summary>
    /// Write <c>Field.AllowZeroLength</c> on a stored empty table's Text field (W-08, task nk97;
    /// <c>evidence/r-q42-field-rule-and/</c>): only <c>VT_BOOL</c> is recorded; other variants refuse by name.
    /// </summary>
    private CallResult PutAllowZeroLength(nint valueArg) => OfTable(t =>
    {
        Foundation.VarType vt = Args.TypeOf(valueArg);
        if (vt != Foundation.VarType.Bool)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.AllowZeroLength put of variant type {vt} (not recorded)"));
        }
        bool allow = Args.Bool(valueArg, omitted: false)!.Value;
        return _owner.Database.SetFieldAllowZeroLength(t, this, _column, allow);
    });

    internal void InvalidateProperties()
    {
        _properties?.Release();
        _properties = null;
    }

    /// <summary>
    /// The 24 built-ins, then a TableDef field's other stored properties (r-dao-1b/field-properties-enumerated: Parent.Name
    /// lists Description, 25). An index field's collection has Count 24, its entries named (r-dao-1b/indexfield-members-each
    /// records only the Count).
    /// </summary>
    private CallResult PropertiesCollection(TableDefObject? t)
    {
        if (t is null)
        {
            _properties ??= new PropertiesObject(this, 24, "the Properties entries of an index field (indexfield-members-each records only Count 24)");
            return Object(_properties);
        }
        if (ColumnFacts.DaoType(_column) is not int type)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Properties of native type {_column.TypeCode}"));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.TableDefField((short)type), ColumnMap(t)));
        return Object(_properties);
    }

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _table?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>The DAO Type and Size of a column, shared by recordset and definition fields (R-FMT-1 FMT1-FAMILIES).</summary>
internal static class ColumnFacts
{
    /// <summary>
    /// DataTypeEnum: native 1-12 and 15 are DAO's own numbers, NUMERIC is dbDecimal 20 (FMT1-DECIMAL); a calculated
    /// column reports its result type, not its stored code (r-cplx-rebuild/calc-schema-change: C_bool 1, C_dec 20).
    /// </summary>
    public static int? DaoType(ColumnDescriptor column) => (column.CalculatedResultType ?? column.TypeCode) switch
    {
        NativeTypes.Numeric => 20,
        NativeTypes.BigInt => 16,
        NativeTypes.ExtendedDateTime => 26,
        var t and (>= 1 and <= 12 or 15) => t,
        _ => null,
    };

    /// <summary>
    /// FieldAttributeEnum of a stored column (R-FMT-1 FMT1-FAMILIES: one value per family in ACCDB and MDB): dbFixedField 1
    /// for a fixed-length column, dbVariableField 2 otherwise, plus dbAutoIncrField 16 for AutoNumber (r-dao-1b/field-members-
    /// typed-each: AutoNumber 17, Text and Memo 2, the numeric, Date, Yes/No and Decimal families 1, OLE and GUID 2;
    /// r-cat-1b/field-members-builtin). A recordset field adds dbUpdatableField 32 when its data is updatable
    /// (r-cursor-1b/option-readonly-write: 33 on a writable dynaset, 1 with dbReadOnly). A calculated column and the
    /// replication, auto-GUID and hyperlink column flags are not recorded: null.
    /// </summary>
    public static int? Attributes(ColumnDescriptor column, bool updatable)
    {
        if (column.CalculatedResultType is not null || (column.Flags & UnrecordedFlags) != 0)
        {
            return null;
        }
        // Fixed vs variable follows the stored TableDef column flag directly: SQL-created GUID has Flags=0x02
        // (IsFixed=false -> dbVariableField 2 in r-dao-1b/field-members-typed-each), while DAO-created GUID has
        // Flags=0x03 (IsFixed=true -> dbFixedField 1 in r-q46-dao-ddl-multi/cf-guid-accdb, cf-guid-mdb, cf-multi-mixed-*).
        int attributes = column.IsFixed ? 1 : 2;
        return attributes | (column.IsAutoNumber ? 16 : 0) | (updatable ? 32 : 0);
    }

    /// <summary>
    /// A field's CollatingOrder is its column's, not the database's: a Decimal's declared precision (r-dao-1b/field-
    /// members-typed-each 38 for a CreateField(name, 20) column, r-fix-fmt-sort/fmt2-scalar-admission-decimal 18); any
    /// other column's sort word (column block bytes 11-12, <see cref="ColumnDescriptor.TypeInfo"/>), 0 reading as
    /// dbSortNeutral 1024 (r-cat-1b/field-members-builtin: 1033 for its Text fields, 1024 for Long, Memo, Date and Yes/No;
    /// field-members-typed-each: 1033 for every family of that file).
    /// </summary>
    public static int CollatingOrder(ColumnDescriptor column) => column.TypeCode == NativeTypes.Numeric
        ? column.NumericPrecision
        : (ushort)column.TypeInfo is 0 ? 1024 : (ushort)column.TypeInfo;

    /// <summary>The column flags no record maps to an attribute: auto-GUID 0x40 and hyperlink 0x80.</summary>
    private const byte UnrecordedFlags = 0x40 | 0x80;

    /// <summary>A stored column property of <paramref name="name"/> in the table's blob (CAT-P), or <paramref name="unset"/> when none is stored.</summary>
    public static CallResult Stored(PropertyBlob? blob, ColumnDescriptor column, string name, CallResult unset)
    {
        StoredProperty? stored = blob?.Map(PropertySectionKind.ColumnValues, column.Name)?.Properties.FirstOrDefault(p => p.Name == name);
        return stored is null ? unset : PropertyObject.StoredValue(stored);
    }

    /// <summary>Size: Text in characters (the column length in bytes, halved), Memo and OLE 0, fixed types their length.</summary>
    public static int? Size(ColumnDescriptor column) => column.TypeCode switch
    {
        NativeTypes.Text => column.Length / 2,
        NativeTypes.Ole or NativeTypes.Memo => 0,
        // Decimal: 16 for the ADO DDL columns (FMT1-DECIMAL, precision 1 to 28), 20 for DAO CreateField's precision 38
        // (r-dec-read/*-daofield-*); other precisions are not observed.
        NativeTypes.Numeric when column.CalculatedResultType is null => column.NumericPrecision is >= 1 and <= 28 ? 16 : column.NumericPrecision == 38 ? 20 : null,
        NativeTypes.BigInt => 8,
        NativeTypes.ExtendedDateTime => 42,
        >= 1 and <= 9 or 15 => column.Length,
        _ => null,
    };
}