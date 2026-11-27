using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
using CalcDb.Foundation;

namespace CalcDb.Com.Dao;

/// <summary>
/// A draft <c>DAO.Index</c> created by <c>TableDef.CreateIndex(Name)</c> on an attached or draft TableDef
/// (W-07, W-08 / R-CAT-1, R-DAO-1; <c>evidence/r-q52-ddl-make-domain/dao-idx-empty-accdb</c>,
/// <c>dao-idx-populated-accdb</c>, <c>dao-idx-delete-accdb</c>, <c>dao-idx-errors-accdb</c>,
/// <c>evidence/r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-accdb</c>). Before <c>Indexes.Append</c>, it accepts puts
/// for <c>Name</c>, <c>Primary</c>, <c>Unique</c>, <c>Required</c>, <c>IgnoreNulls</c>, exposes <c>Fields</c>
/// (<see cref="NewIndexFieldsObject"/>) and <c>CreateField(Name)</c> (<see cref="NewIndexFieldObject"/>). Once
/// appended to an attached TableDef's <c>Indexes</c>, it binds to the persisted <see cref="IndexObject"/> so
/// subsequent property puts fail 3268, <c>Fields.Append</c> fails 3219, re-append fails 3367, and <c>Indexes.Delete</c>
/// invalidates it (3420).
/// </summary>
internal sealed unsafe class NewIndexObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Index;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => ForwardOrDraft(self, "Name", InvokeKind.PropertyGet, args, x => Text(x.Name)))
        .Put("Name", (self, args) => { nint v = args[^1]; return ForwardOrDraft(self, "Name", InvokeKind.PropertyPut, args, x => { x.Name = Args.Text(v) ?? ""; return CallResult.Nothing; }); })
        .Get("Primary", (self, args) => ForwardOrDraft(self, "Primary", InvokeKind.PropertyGet, args, x => Bool(x.Primary)))
        .Put("Primary", (self, args) => { nint v = args[^1]; return ForwardOrDraft(self, "Primary", InvokeKind.PropertyPut, args, x => x.SetBool(v, b => x.Primary = b, "Primary")); })
        .Get("Unique", (self, args) => ForwardOrDraft(self, "Unique", InvokeKind.PropertyGet, args, x => Bool(x.Unique)))
        .Put("Unique", (self, args) => { nint v = args[^1]; return ForwardOrDraft(self, "Unique", InvokeKind.PropertyPut, args, x => x.SetBool(v, b => x.Unique = b, "Unique")); })
        .Get("Required", (self, args) => ForwardOrDraft(self, "Required", InvokeKind.PropertyGet, args, x => Bool(x.Required)))
        .Put("Required", (self, args) => { nint v = args[^1]; return ForwardOrDraft(self, "Required", InvokeKind.PropertyPut, args, x => x.SetBool(v, b => x.Required = b, "Required")); })
        .Get("IgnoreNulls", (self, args) => ForwardOrDraft(self, "IgnoreNulls", InvokeKind.PropertyGet, args, x => Bool(x.IgnoreNulls)))
        .Put("IgnoreNulls", (self, args) => { nint v = args[^1]; return ForwardOrDraft(self, "IgnoreNulls", InvokeKind.PropertyPut, args, x => x.SetBool(v, b => x.IgnoreNulls = b, "IgnoreNulls")); })
        .Get("Clustered", (self, args) => ForwardOrDraft(self, "Clustered", InvokeKind.PropertyGet, args, _ => Bool(false)))
        .Get("Foreign", (self, args) => ForwardOrDraft(self, "Foreign", InvokeKind.PropertyGet, args, _ => Bool(false)))
        // r-dao-1b/index-create-readback calls 18, 27, 36-38: a newly appended Index reads DistinctCount 0 until Indexes.Refresh rebinds it.
        .Get("DistinctCount", (self, args) =>
        {
            var x = (NewIndexObject)self;
            if (x.Invalid)
            {
                return x.Fail(3420, DaoErrors.ObjectInvalid);
            }
            if (x._attachedIndex is { } attached && x._refreshedAfterAttach)
            {
                return attached.Run(Decl.Member("DistinctCount", InvokeKind.PropertyGet), args);
            }
            return Int32(0);
        })
        // r-dao-1b/index-create-readback calls 11, 34-35: Fields keeps its COM identity across Indexes.Append and yields the same held Field wrappers.
        .Get("Fields", (self, args) =>
        {
            var x = (NewIndexObject)self;
            return x.Invalid ? x.Fail(3420, DaoErrors.ObjectInvalid) : Object(x.Fields);
        })
        .Get("Properties", (self, args) => ForwardOrDraft(self, "Properties", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftIndexProperties", "Index.Properties on a draft Index is not recorded"))))
        .Method("CreateField", (self, args) => { nint name = args.Length > 0 ? args[0] : 0; return ForwardOrDraft(self, "CreateField", InvokeKind.Method, args, x => NewIndexFieldObject.Create(x.Engine, name)); });

    private DefinitionCollection? _attachedOwner;
    private IndexObject? _attachedIndex;
    private bool _refreshedAfterAttach;

    public NewIndexObject(EngineState engine, string name) : base(Decl, Vtable, engine, "DAO.Index")
    {
        Name = name;
        Fields = new NewIndexFieldsObject(this);
    }

    public string Name { get; private set; }

    public bool Primary { get; private set; }

    public bool Unique { get; private set; }

    public bool Required { get; private set; }

    public bool IgnoreNulls { get; private set; }

    public NewIndexFieldsObject Fields { get; }

    /// <summary>The draft TableDef's Indexes collection this index was appended to, or null.</summary>
    public NewIndexesObject? AppendedTo { get; set; }

    /// <summary>True once appended to an attached TableDef's Indexes collection.</summary>
    public bool IsAttached => _attachedIndex is not null;

    public override bool Invalid => _attachedIndex is { Invalid: true };

    protected override OperationTable Table => Operations;

    public static CallResult Create(EngineState engine, nint nameArg)
    {
        string indexName = nameArg == 0 ? "" : Args.Text(nameArg) ?? "";
        var index = new NewIndexObject(engine, indexName);
        CallResult result = Object(index);
        index.Release();
        return result;
    }

    public NewIndex ToNewIndex()
    {
        string[] fieldNames = new string[Fields.Members.Count];
        bool[]? descending = null;
        for (int i = 0; i < Fields.Members.Count; i++)
        {
            NewIndexFieldObject f = Fields.Members[i];
            fieldNames[i] = f.Name;
            if ((f.Attributes & 1) != 0)
            {
                descending ??= new bool[Fields.Members.Count];
                descending[i] = true;
            }
        }
        return new NewIndex(
            Name,
            fieldNames,
            PrimaryKey: Primary,
            Unique: Unique,
            DisallowNull: Required,
            IgnoreNull: IgnoreNulls,
            Descending: descending);
    }

    public void Attach(DefinitionCollection owner, TableDefinition table, LogicalIndex logical)
    {
        _attachedOwner = owner;
        _attachedIndex = new IndexObject(owner, table, logical);
        _refreshedAfterAttach = false;
    }

    public void Rebind(TableDefinition table, LogicalIndex logical) => _attachedIndex?.Rebind(table, logical);

    /// <summary>Called only on an explicit <c>TableDef.Indexes.Refresh</c> (<c>r-dao-1b/index-create-readback</c> call 36).</summary>
    public void MarkRefreshed() => _refreshedAfterAttach = true;

    public void InvalidateDeleted() => _attachedIndex?.InvalidateDeleted();

    private CallResult SetBool(nint value, Action<bool> assign, string propName)
    {
        if (Args.TypeOf(value) != VarType.Bool)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftIndexProperty", $"draft Index.{propName} put of variant type {Args.TypeOf(value)} is not recorded"));
        }
        assign(Args.Bool(value, omitted: false)!.Value);
        return CallResult.Nothing;
    }

    private static CallResult ForwardOrDraft(ComObject self, string memberName, InvokeKind kind, ReadOnlySpan<nint> args, Func<NewIndexObject, CallResult> draftOp)
    {
        var x = (NewIndexObject)self;
        if (x._attachedIndex is { } attached)
        {
            return attached.Run(Decl.Member(memberName, kind), args);
        }
        return draftOp(x);
    }

    protected override void OnFinalRelease()
    {
        Fields.Release();
        if (_attachedOwner is not null)
        {
            _attachedOwner.Released(this);
            _attachedOwner = null;
        }
        if (_attachedIndex is not null)
        {
            _attachedIndex.Release();
            _attachedIndex = null;
        }
    }
}

/// <summary>The <c>Indexes</c> collection of a draft <see cref="NewTableDefObject"/> (<c>r-q52-ddl-make-domain/dao-idx-empty-*</c>, <c>dao-idx-errors-*</c>).</summary>
internal sealed unsafe class NewIndexesObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Indexes;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, c => Int16(c._members.Count)))
        .Method("Append", (self, args) => { nint index = args[0]; return Live(self, c => c.Append(index)); })
        .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing));

    private readonly NewTableDefObject _table;
    private readonly List<NewIndexObject> _members = [];

    public NewIndexesObject(NewTableDefObject table) : base(Decl, Vtable, table.Engine, "DAO.Indexes") => _table = table;

    public IReadOnlyList<NewIndexObject> Members => _members;

    public override bool Invalid => _table.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewIndexesObject, CallResult> op)
    {
        var c = (NewIndexesObject)self;
        return c.Invalid ? c.Fail(3420, DaoErrors.ObjectInvalid) : op(c);
    }

    private CallResult Append(nint arg)
    {
        if (Args.ObjectOf<NewIndexObject>(arg) is not { } index)
        {
            return CallResult.Fail(Failure.NotImplemented("Indexes.Append of an argument that is not a CalcDb draft Index"));
        }
        if (index.AppendedTo is not null || index.IsAttached)
        {
            return Fail(3367, DaoErrors.AlreadyInCollection);
        }
        index.AddRef();
        index.AppendedTo = this;
        _members.Add(index);
        return CallResult.Nothing;
    }

    protected override void OnFinalRelease()
    {
        foreach (NewIndexObject ix in _members)
        {
            ix.Release();
        }
        _members.Clear();
    }
}

/// <summary>The <c>IndexFields</c> collection of a draft <see cref="NewIndexObject"/> (<c>r-q52-ddl-make-domain/dao-idx-*</c>).</summary>
internal sealed unsafe class NewIndexFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.IndexFields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, c => Int16(c._members.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, c => c.Item(index)); })
        .Method("Append", (self, args) => { nint field = args[0]; return Live(self, c => c.Append(field)); })
        .Method("Delete", (self, args) => Live(self, c => c._index.IsAttached
            ? c.Fail(3219, DaoErrors.InvalidOperation)
            : CallResult.Fail(Failure.NotModelled("NotModelled.DraftIndexFieldsDelete", "Delete on a draft Index.Fields collection is not recorded"))))
        .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing));

    private readonly NewIndexObject _index;
    private readonly List<NewIndexFieldObject> _members = [];

    public NewIndexFieldsObject(NewIndexObject index) : base(Decl, Vtable, index.Engine, "DAO.Fields") => _index = index;

    public IReadOnlyList<NewIndexFieldObject> Members => _members;

    public bool IsAttached => _index.IsAttached;

    public override bool Invalid => _index.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewIndexFieldsObject, CallResult> op)
    {
        var c = (NewIndexFieldsObject)self;
        return c.Invalid ? CallResult.Fail(Failure.Dao(3420, "DAO.Index", DaoErrors.ObjectInvalid)) : op(c);
    }

    private CallResult Item(nint index)
    {
        int? ordinal = CollectionLookup.Find(index, new CollectionNames([.. _members.Select(m => m.Name)]), out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("IndexFields.Item with an object index"));
        }
        return ordinal is int i ? Object(_members[i]) : Fail(3265, DaoErrors.ItemNotFound);
    }

    private CallResult Append(nint arg)
    {
        if (_index.IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.ObjectOf<NewIndexFieldObject>(arg) is not { } field)
        {
            return CallResult.Fail(Failure.NotImplemented("Index.Fields.Append of an argument that is not a CalcDb draft Index Field"));
        }
        if (field.AppendedTo is not null)
        {
            return Fail(3367, DaoErrors.AlreadyInCollection);
        }
        field.AddRef();
        field.AppendedTo = this;
        _members.Add(field);
        return CallResult.Nothing;
    }

    protected override void OnFinalRelease()
    {
        foreach (NewIndexFieldObject f in _members)
        {
            f.Release();
        }
        _members.Clear();
    }
}

/// <summary>A draft index <c>Field</c> created by <c>Index.CreateField(Name)</c> (<c>r-q52-ddl-make-domain/dao-idx-*</c>).</summary>
internal sealed unsafe class NewIndexFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Name)))
        .Put("Name", (self, args) => { nint v = args[^1]; return Live(self, f => f.AppendedTo is { IsAttached: true }
            ? CallResult.Fail(Failure.NotModelled("NotModelled.AttachedIndexFieldPut", "Index Field.Name put after Indexes.Append is not recorded"))
            : f.SetName(v)); })
        .Get("Attributes", (self, args) => Live(self, f => Int32(f.Attributes)))
        .Put("Attributes", (self, args) => { nint v = args[^1]; return Live(self, f => f.AppendedTo is { IsAttached: true }
            ? CallResult.Fail(Failure.NotModelled("NotModelled.AttachedIndexFieldPut", "Index Field.Attributes put after Indexes.Append is not recorded"))
            : f.SetAttributes(v)); });

    public NewIndexFieldObject(EngineState engine, string name) : base(Decl, Vtable, engine, "DAO.Field") => Name = name;

    public string Name { get; private set; }

    /// <summary>0 (ascending) or 1 (<c>dbDescending</c>; <c>r-q52-ddl-make-domain/dao-idx-empty-accdb</c>).</summary>
    public int Attributes { get; private set; }

    public NewIndexFieldsObject? AppendedTo { get; set; }

    public override bool Invalid => AppendedTo is { Invalid: true };

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewIndexFieldObject, CallResult> op)
    {
        var f = (NewIndexFieldObject)self;
        return f.Invalid ? f.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    private CallResult SetName(nint value)
    {
        Name = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    public static CallResult Create(EngineState engine, nint nameArg)
    {
        string fieldName = nameArg == 0 ? "" : Args.Text(nameArg) ?? "";
        var field = new NewIndexFieldObject(engine, fieldName);
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    private CallResult SetAttributes(nint value)
    {
        if (Args.Int32(value, null) is not (0 or 1) and var attr)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftIndexFieldAttributes", $"draft Index Field Attributes put of {attr?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "non-integer"} is not recorded (0 and dbDescending=1 are recorded)"));
        }
        Attributes = Args.Int32(value, null)!.Value;
        return CallResult.Nothing;
    }
}
