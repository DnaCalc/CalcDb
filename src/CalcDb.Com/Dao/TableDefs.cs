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
    /// The membership as the Database lists it now, for a collection whose members a catalog change alters (TableDefs);
    /// null for a fixed collection. The Database relists its one TableDefs object after each catalog change, so a client
    /// that holds it, or fetches it again, sees the new table (r-q15-ddl-open/lv-rewrite-byte-n: db.TableDefs after the
    /// Append is the same object, u6, and Item("T") finds the new table; r-cat-1/names: Refresh after the Appends, Count 11).
    /// </summary>
    public Func<(IReadOnlyList<string> Names, Func<DefinitionCollection, int, DaoObject> Make)>? Membership { get; init; }

    /// <summary>List the members again from <see cref="Membership"/>; held member wrappers stay valid for their clients.</summary>
    public void Relist()
    {
        if (Membership is { } membership)
        {
            (IReadOnlyList<string> names, Func<DefinitionCollection, int, DaoObject> make) = membership();
            (_names, _lookup, _make) = (names, new CollectionNames(names), make);
            _held.Clear(); // the ordinals change
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

    /// <summary>The collection's Append (TableDefs.Append, W-07/native-metadata-write); null where the slice has none.</summary>
    public Func<DefinitionCollection, nint, CallResult>? Appender { get; init; }

    /// <summary>The failure every member gives instead of an answer (number and text), or null.</summary>
    public (int Number, string Text)? Denied { get; init; }

    /// <summary>A named gap: when set, Count and Item give it as not implemented (a membership CalcDb cannot yet decide).</summary>
    public string? Gap { get; init; }

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
    public (int Number, string Text)? CountDenied { get; init; }

    /// <summary>A named gap of Item alone, when Count answers (or fails as ACE does) and Item is not recorded.</summary>
    public string? ItemGap { get; init; }

    public override bool Invalid => _database.Closed;

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
                    .Method("Refresh", (self, args) => Live(self, c => { c.Relist(); return CallResult.Nothing; }));
                if (decl.MembersNamed("Append").Length > 0)
                {
                    operations = operations.Method("Append", (self, args) => { nint item = args[0]; return Live(self, c => c.Appender is { } append ? append(c, item) : CallResult.Fail(Failure.NotImplemented(c.Source + ".Append"))); });
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
        if (c._database.Closed)
        {
            return CallResult.Fail(Failure.Dao(3420, c.ClosedSource ?? c.Source, DaoErrors.ObjectInvalid));
        }
        return c.Denied is { } denied ? c.Fail(denied.Number, denied.Text) : op(c);
    }

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
    private const int SystemObjectFlag = unchecked((int)0x80000000);

    private static readonly InterfaceDecl Decl = DaoTypes.TableDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, t => Text(t._entry.Name)))
        .Get("Attributes", (self, args) => Live(self, t => Int32(t._entry.Flags)))
        .Get("Connect", (self, args) => Live(self, t => Text("")))
        .Get("SourceTableName", (self, args) => Live(self, t => Text("")))
        .Get("Updatable", (self, args) => Live(self, t => Bool(!t._owner.Database.ReadOnly)))
        .Get("DateCreated", (self, args) => Live(self, t => CatalogDates.Date(t._entry.DateCreateBits)))
        .Get("LastUpdated", (self, args) => Live(self, t => CatalogDates.Date(t._entry.DateUpdateBits)))
        .Get("RecordCount", (self, args) => Live(self, t => t.Definition is { } d ? Int32((int)d.RowCount) : t.DefinitionFailure()))
        .Get("ValidationRule", (self, args) => Live(self, t => t.StoredText("ValidationRule")))
        .Get("ValidationText", (self, args) => Live(self, t => t.StoredText("ValidationText")))
        .Get("Properties", (self, args) => Live(self, t => t.PropertiesCollection()))
        .Get("ConflictTable", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("ReplicaFilter", (self, args) => Live(self, t => t.Fail(3032, DaoErrors.CannotPerform)))
        .Get("Fields", (self, args) => Live(self, t => t.FieldsCollection()))
        .Get("Indexes", (self, args) => Live(self, t => t.IndexesCollection()));

    private readonly DefinitionCollection _owner;
    private readonly CatalogEntry _entry;
    private DefinitionCollection? _fields;
    private DefinitionCollection? _indexes;
    private PropertiesObject? _properties;

    public TableDefObject(DefinitionCollection owner, CatalogEntry entry) : base(Decl, Vtable, owner.Engine, "DAO.TableDef")
    {
        _owner = owner;
        _entry = entry;
        owner.AddRef();
        Definition = owner.Database.Definition(entry, out _definitionFailure);
        Blob = owner.Database.Blob(entry, out _blobFailure);
    }

    private readonly string? _definitionFailure;
    private readonly string? _blobFailure;

    /// <summary>The table's MR2 property blob (CAT-P): its own map and one map per column with stored properties.</summary>
    public PropertyBlob? Blob { get; }

    public string Name => _entry.Name;

    public override bool Invalid => _owner.Database.Closed;

    public TableDefinition? Definition { get; }

    private bool IsSystem => (_entry.Flags & SystemObjectFlag) != 0;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<TableDefObject, CallResult> op)
    {
        var t = (TableDefObject)self;
        return t._owner.Database.Closed ? t.Fail(3420, DaoErrors.ObjectInvalid) : op(t);
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

    private CallResult FieldsCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        IReadOnlyList<ColumnDescriptor> columns = IsSystem ? [] : d.Columns;
        _fields ??= new DefinitionCollection(DaoTypes.Fields, "DAO.Fields", _owner.Database, [.. columns.Select(c => c.Name)],
            (c, i) => new DefinitionFieldObject(c, this, columns[i], null, i))
        {
            EmptyNameSource = "DAO.TableDef", // r-dao-1-err-1/coll-name-variants: Item("") of a TableDef's Fields
        };
        return Object(_fields);
    }

    private CallResult IndexesCollection()
    {
        if (Definition is not { } d)
        {
            return DefinitionFailure();
        }
        if (_indexes is null)
        {
            LogicalIndex[] listed = [];
            if (!IsSystem)
            {
                LogicalIndex[]? sorted = _owner.Database.Collation is { } order
                    ? ListedIndexes.Of(d.LogicalIndexes, _owner.Database.Format.IndexDescriptor, order)
                    : ListedIndexes.OfPlainNames(d.LogicalIndexes, _owner.Database.Format.IndexDescriptor);
                if (sorted is null)
                {
                    return CallResult.Fail(Failure.NotModelled("NotModelled.IndexNameOrder", $"the Indexes of '{_entry.Name}' in a sort order outside the General family, with names whose order depends on it (FMT1-INDEX-ENUM was asked under General)"));
                }
                listed = sorted;
            }
            _indexes = new DefinitionCollection(DaoTypes.Indexes, "DAO.Indexes", _owner.Database, [.. listed.Select(i => i.Name)],
                (c, i) => new IndexObject(c, d, listed[i]))
            {
                Denied = IsSystem ? (3110, DaoErrors.NoReadDefinitions(_entry.Name)) : null,
                ClosedSource = "DAO.TableDef", // 3420 after Close from the owner (r-dao-1-err-1/life-database-workspace-close)
            };
        }
        return Object(_indexes);
    }

    protected override void OnFinalRelease()
    {
        _fields?.Release();
        _indexes?.Release();
        _properties?.Release();
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
        .Get("Foreign", (self, args) => Live(self, x => Bool(x._logical.IndexType == x.Rules.ForeignKeyType)))
        .Get("Unique", (self, args) => Live(self, x => Bool(x._physical.Unique(x.Rules))))
        .Get("IgnoreNulls", (self, args) => Live(self, x => Bool(x._physical.IgnoreNulls(x.Rules))))
        .Get("Required", (self, args) => Live(self, x => Bool(x._physical.Required(x.Rules))))
        .Get("Clustered", (self, args) => Live(self, x => Bool(false)))
        .Get("DistinctCount", (self, args) => Live(self, x => x.DistinctCount()))
        .Get("Fields", (self, args) => Live(self, x => x.FieldsCollection()))
        .Get("Properties", (self, args) => Live(self, x => Object(x._properties ??= new PropertiesObject(x, BuiltInProperties.Index))));

    private readonly DefinitionCollection _owner;
    private readonly TableDefinition _table;
    private readonly LogicalIndex _logical;
    private readonly PhysicalIndex _physical;
    private DefinitionCollection? _fields;
    private PropertiesObject? _properties;

    public override bool Invalid => _owner.Database.Closed;

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
            (c, i) => new DefinitionFieldObject(c, null, columns[i], _physical.Columns[i], i));
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
        .Get("ValidationRule", (self, args) => Live(self, f => f.Stored("ValidationRule", Text(""))))
        .Get("ValidationText", (self, args) => Live(self, f => f.Stored("ValidationText", Text(""))))
        .Get("Required", (self, args) => Live(self, f => f.Stored("Required", Bool(false))))
        .Get("AllowZeroLength", (self, args) => Live(self, f => f.Stored("AllowZeroLength", Bool(false))))
        .Get("AppendOnly", (self, args) => Live(self, f => f.OfTable(t => Bool(false))))
        .Get("Expression", (self, args) => Live(self, f => f.OfTable(t => Text(""))))
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

    public override bool Invalid => _owner.Database.Closed;

    protected override OperationTable Table => Operations;

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
        // A GUID column reads dbVariableField whatever its stored length flag (field-members-typed-each: TGuid 2).
        int attributes = column.IsFixed && column.TypeCode != NativeTypes.Guid ? 1 : 2;
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
        >= 1 and <= 9 or 15 => column.Length,
        _ => null,
    };
}