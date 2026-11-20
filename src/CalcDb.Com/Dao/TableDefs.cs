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
    private readonly IReadOnlyList<string> _names;
    private readonly CollectionNames _lookup;
    private readonly Func<DefinitionCollection, int, DaoObject> _make;
    private readonly Dictionary<int, DaoObject> _held = [];

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

    /// <summary>The failure every member gives instead of an answer (number and text), or null.</summary>
    public (int Number, string Text)? Denied { get; init; }

    public DatabaseObject Database => _database;

    protected override OperationTable Table => _shape.Operations;

    private static (nint, OperationTable) Shape(InterfaceDecl decl)
    {
        lock (Shapes)
        {
            if (!Shapes.TryGetValue(decl, out (nint, OperationTable) shape))
            {
                shape = (DualEntries.Build(decl), new OperationTable(decl)
                    .Get("Count", (self, args) => Live(self, c => Int16(c._names.Count)))
                    .Get("Item", (self, args) => { nint index = args[0]; return Live(self, c => c.Item(index)); })
                    .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing)));
                Shapes[decl] = shape;
            }
            return shape;
        }
    }

    private static CallResult Live(ComObject self, Func<DefinitionCollection, CallResult> op)
    {
        var c = (DefinitionCollection)self;
        if (c._database.Closed)
        {
            return c.Fail(3420, DaoErrors.ObjectInvalid);
        }
        return c.Denied is { } denied ? c.Fail(denied.Number, denied.Text) : op(c);
    }

    private CallResult Item(nint index)
    {
        int? ordinal = CollectionLookup.Find(index, _lookup, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("a definition collection's Item with an object index"));
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
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
    private const int SystemObjectFlag = unchecked((int)0x80000000);

    private static readonly InterfaceDecl Decl = DaoTypes.TableDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, t => Text(t._entry.Name)))
        .Get("Attributes", (self, args) => Live(self, t => Int32(t._entry.Flags)))
        .Get("Connect", (self, args) => Live(self, t => Text("")))
        .Get("SourceTableName", (self, args) => Live(self, t => Text("")))
        .Get("Updatable", (self, args) => Live(self, t => Bool(false)))
        .Get("DateCreated", (self, args) => Live(self, t => Date(t._entry.DateCreateBits)))
        .Get("LastUpdated", (self, args) => Live(self, t => Date(t._entry.DateUpdateBits)))
        .Get("RecordCount", (self, args) => Live(self, t => t.Definition is { } d ? Int32((int)d.RowCount) : t.DefinitionFailure()))
        .Get("ValidationRule", (self, args) => Live(self, t => t.NoProperties()))
        .Get("ValidationText", (self, args) => Live(self, t => t.NoProperties()))
        .Get("ConflictTable", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("ReplicaFilter", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("Fields", (self, args) => Live(self, t => t.FieldsCollection()))
        .Get("Indexes", (self, args) => Live(self, t => t.IndexesCollection()));

    private readonly DefinitionCollection _owner;
    private readonly CatalogEntry _entry;
    private DefinitionCollection? _fields;
    private DefinitionCollection? _indexes;

    public TableDefObject(DefinitionCollection owner, CatalogEntry entry) : base(Decl, Vtable, owner.Engine, "DAO.TableDef")
    {
        _owner = owner;
        _entry = entry;
        owner.AddRef();
        Definition = owner.Database.Definition(entry, out _definitionFailure);
    }

    private readonly string? _definitionFailure;

    public TableDefinition? Definition { get; }

    private bool IsSystem => (_entry.Flags & SystemObjectFlag) != 0;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<TableDefObject, CallResult> op)
    {
        var t = (TableDefObject)self;
        return t._owner.Database.Closed ? t.Fail(3420, DaoErrors.ObjectInvalid) : op(t);
    }

    private CallResult DefinitionFailure() => CallResult.Fail(Failure.NotImplemented("TableDef member when the TableDef cannot be read: " + _definitionFailure));

    private static CallResult Date(ulong? bits) => bits is ulong b
        ? CallResult.Of(Values.OwnedVariant.FromDate(BitConverter.Int64BitsToDouble((long)b)))
        : CallResult.Fail(Failure.NotImplemented("TableDef date of a catalog row without that field"));

    /// <summary>ValidationRule and ValidationText are "" for a table without stored properties; reading them from a property blob is CAT-P (W-04/row-catalog-algorithms).</summary>
    private CallResult NoProperties() => _entry.Properties is null ? Text("") : CallResult.Fail(Failure.NotImplemented("TableDef validation properties from an MR2 property blob (CAT-P)"));

    private CallResult FieldsCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        IReadOnlyList<ColumnDescriptor> columns = IsSystem ? [] : d.Columns;
        _fields ??= new DefinitionCollection(DaoTypes.Fields, "DAO.Fields", _owner.Database, [.. columns.Select(c => c.Name)],
            (c, i) => new DefinitionFieldObject(c, columns[i], null, i));
        return Object(_fields);
    }

    private CallResult IndexesCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        _indexes ??= new DefinitionCollection(DaoTypes.Indexes, "DAO.Indexes", _owner.Database, IsSystem ? [] : [.. d.LogicalIndexes.Select(i => i.Name)],
            (c, i) => new IndexObject(c, d, d.LogicalIndexes[i]))
        {
            Denied = IsSystem ? (3110, DaoErrors.NoReadDefinitions(_entry.Name)) : null,
        };
        return Object(_indexes);
    }

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _indexes?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// An Index of a TableDef (II-05 §1; r-cat-1b/index-members-ddl): Primary and Foreign from the logical index type,
/// Unique, IgnoreNulls and Required from the physical tree's flags, Clustered False, DistinctCount the tree's stored
/// counter (II-05 §1.1: not a current cardinality; 0 in every observed index), and its Fields.
/// </summary>
internal sealed unsafe class IndexObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Index;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, x => Text(x._logical.Name)))
        .Get("Primary", (self, args) => Live(self, x => Bool(x._logical.IndexType == x.Rules.PrimaryKeyType)))
        .Get("Foreign", (self, args) => Live(self, x => Bool(x._logical.IndexType == x.Rules.ForeignKeyType)))
        .Get("Unique", (self, args) => Live(self, x => Bool(x._physical.Unique(x.Rules))))
        .Get("IgnoreNulls", (self, args) => Live(self, x => Bool(x._physical.IgnoreNulls(x.Rules))))
        .Get("Required", (self, args) => Live(self, x => Bool(x._physical.Required(x.Rules))))
        .Get("Clustered", (self, args) => Live(self, x => Bool(false)))
        .Get("DistinctCount", (self, args) => Live(self, x => Int32((int)x._physical.UniqueEntryCount)))
        .Get("Fields", (self, args) => Live(self, x => x.FieldsCollection()));

    private readonly DefinitionCollection _owner;
    private readonly TableDefinition _table;
    private readonly LogicalIndex _logical;
    private readonly PhysicalIndex _physical;
    private DefinitionCollection? _fields;

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
        return x._owner.Database.Closed ? x.Fail(3420, DaoErrors.ObjectInvalid) : op(x);
    }

    private CallResult FieldsCollection()
    {
        ColumnDescriptor[] columns = [.. _physical.Columns.Select(ic => _table.Columns.First(c => c.PhysicalNumber == ic.PhysicalNumber))];
        _fields ??= new DefinitionCollection(DaoTypes.IndexFields, "DAO.IndexFields", _owner.Database, [.. columns.Select(c => c.Name)],
            (c, i) => new DefinitionFieldObject(c, columns[i], _physical.Columns[i], i));
        return Object(_fields);
    }

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _owner.Released(this);
        _owner.Release();
    }
}

/// <summary>
/// A Field of a definition (a TableDef's or an Index's Fields; Field3): Name, Type, Size and OrdinalPosition from the
/// column; an index field's Attributes is dbDescending (1) for a descending column, else 0 (r-cat-1b/index-members-ddl).
/// A TableDef field's Attributes (fixed, variable, AutoNumber, updatable bits) is not yet derived.
/// </summary>
internal sealed unsafe class DefinitionFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f._column.Name)))
        .Get("Type", (self, args) => Live(self, f => f._column.TypeCode is >= 1 and <= 12 or 15 ? Int16(f._column.TypeCode) : CallResult.Fail(Failure.NotImplemented($"Field.Type of native type {f._column.TypeCode}"))))
        .Get("OrdinalPosition", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("Attributes", (self, args) => Live(self, f => f._indexColumn is { } ic ? Int32(ic.Ascending ? 0 : 1) : CallResult.Fail(Failure.NotImplemented("TableDef Field.Attributes"))));

    private readonly DefinitionCollection _owner;
    private readonly ColumnDescriptor _column;
    private readonly IndexColumn? _indexColumn;
    private readonly int _ordinal;

    public DefinitionFieldObject(DefinitionCollection owner, ColumnDescriptor column, IndexColumn? indexColumn, int ordinal) : base(Decl, Vtable, owner.Engine, "DAO.Field")
    {
        _owner = owner;
        _column = column;
        _indexColumn = indexColumn;
        _ordinal = ordinal;
        owner.AddRef();
    }

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<DefinitionFieldObject, CallResult> op)
    {
        var f = (DefinitionFieldObject)self;
        return f._owner.Database.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    protected override void OnFinalRelease()
    {
        _owner.Released(this);
        _owner.Release();
    }
}
