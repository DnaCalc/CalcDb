using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;

namespace CalcDb.Com.Dao;

/// <summary>
/// A draft Relation made by <c>Database.CreateRelation(Name, Table, ForeignTable, Attributes)</c> and not yet appended
/// (II-12 DAO definition objects; W-07 / R-CAT-1, R-DAO-1; evidence/r-q46-dao-ddl-multi/rel-*,
/// evidence/r-q47-bigint-expr-edges/rel-*, evidence/r-dao-1-err-1/draft-index-relation): holds Name, Table (primary
/// table), ForeignTable (foreign/child table), Attributes (<c>dbRelationUnique=1</c>, <c>dbRelationDontEnforce=2</c>,
/// <c>dbRelationUpdateCascade=256</c>, <c>dbRelationDeleteCascade=4096</c>, or <c>4352</c>), and draft Fields
/// (<c>Name</c> = parent column, <c>ForeignName</c> = child column).
/// </summary>
internal sealed unsafe class NewRelationObject : DaoObject
{
    public const int DbRelationUnique = 1;
    public const int DbRelationDontEnforce = 2;
    public const int DbRelationUpdateCascade = 256;
    public const int DbRelationDeleteCascade = 4096;

    private static readonly InterfaceDecl Decl = DaoTypes.Relation;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, r => Text(r.Name)))
        .Put("Name", (self, args) => { nint v = args[^1]; return Live(self, r => { r.Name = Args.Text(v) ?? ""; return CallResult.Nothing; }); })
        .Get("Table", (self, args) => Live(self, r => Text(r.PrimaryTable)))
        .Put("Table", (self, args) => { nint v = args[^1]; return Live(self, r => { r.PrimaryTable = Args.Text(v) ?? ""; return CallResult.Nothing; }); })
        .Get("ForeignTable", (self, args) => Live(self, r => Text(r.ForeignTable)))
        .Put("ForeignTable", (self, args) => { nint v = args[^1]; return Live(self, r => { r.ForeignTable = Args.Text(v) ?? ""; return CallResult.Nothing; }); })
        .Get("Attributes", (self, args) => Live(self, r => Int32(r.Attributes)))
        .Put("Attributes", (self, args) => { nint v = args[^1]; return Live(self, r => r.SetAttributes(v)); })
        .Get("Fields", (self, args) => Live(self, r => Object(r.Fields)))
        .Method("CreateField", (self, args) => { nint name = args.Length > 0 ? args[0] : 0; return Live(self, r => r.CreateField(name)); });

    private readonly DatabaseObject _database;

    public NewRelationObject(DatabaseObject database, string name, string table, string foreignTable, int attributes)
        : base(Decl, Vtable, database.Engine, "DAO.Relation")
    {
        _database = database;
        Name = name;
        PrimaryTable = table;
        ForeignTable = foreignTable;
        Attributes = attributes;
        Created = DateTime.Now;
        Fields = new NewRelationFieldsObject(this);
    }

    public string Name { get; private set; }

    public string PrimaryTable { get; private set; }

    public string ForeignTable { get; private set; }

    public int Attributes { get; private set; }

    public DateTime Created { get; }

    public NewRelationFieldsObject Fields { get; }

    public override bool Invalid => _database.Closed;

    protected override OperationTable Table => Operations;

    protected override void OnFinalRelease() => Fields.Release();

    private static CallResult Live(ComObject self, Func<NewRelationObject, CallResult> op)
    {
        var r = (NewRelationObject)self;
        return r._database.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    private CallResult SetAttributes(nint value)
    {
        if (Args.Int32(value, null) is not int attr)
        {
            return CallResult.Fail(Failure.NotImplemented("Relation.Attributes put of a value that does not convert to an integer"));
        }
        Attributes = attr;
        return CallResult.Nothing;
    }

    private CallResult CreateField(nint nameArg)
    {
        string fieldName = nameArg == 0 ? "" : Args.Text(nameArg) ?? "";
        var field = new NewRelationFieldObject(this, fieldName);
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    /// <summary>Lower this draft Relation into an <see cref="AddForeignKeyRequest"/> on <see cref="SchemaRoute.Dao"/>.</summary>
    public AddForeignKeyRequest? Request(out string? notWritten)
    {
        notWritten = null;
        if (Attributes is not (0 or DbRelationUnique or DbRelationDontEnforce or DbRelationUpdateCascade or DbRelationDeleteCascade or (DbRelationUpdateCascade | DbRelationDeleteCascade)))
        {
            notWritten = $"Relations.Append with Attributes={Attributes} (only 0, dbRelationUnique=1, dbRelationDontEnforce=2, dbRelationUpdateCascade=256, dbRelationDeleteCascade=4096, and 4352 are recorded)";
            return null;
        }
        List<string> parentCols = new(Fields.Members.Count);
        List<string> childCols = new(Fields.Members.Count);
        foreach (NewRelationFieldObject f in Fields.Members)
        {
            if (f.Name.Length == 0 || f.ForeignName.Length == 0)
            {
                notWritten = $"Relations.Append with empty relation field Name ('{f.Name}') or ForeignName ('{f.ForeignName}') is not recorded";
                return null;
            }
            parentCols.Add(f.Name);
            childCols.Add(f.ForeignName);
        }
        var fk = new NewForeignKey(
            Name,
            childCols,
            PrimaryTable,
            parentCols,
            OnDeleteCascade: (Attributes & DbRelationDeleteCascade) != 0,
            OnUpdateCascade: (Attributes & DbRelationUpdateCascade) != 0,
            Unique: (Attributes & DbRelationUnique) != 0,
            DontEnforce: (Attributes & DbRelationDontEnforce) != 0);
        return new AddForeignKeyRequest(ForeignTable, fk, SchemaRoute.Dao);
    }
}

/// <summary>The Fields collection of a draft <see cref="NewRelationObject"/>.</summary>
internal sealed unsafe class NewRelationFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, c => Int16(c._members.Count)))
        .Method("Append", (self, args) => { nint field = args[0]; return Live(self, c => c.Append(field)); })
        .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing));

    private readonly NewRelationObject _relation;
    private readonly List<NewRelationFieldObject> _members = [];

    public NewRelationFieldsObject(NewRelationObject relation) : base(Decl, Vtable, relation.Engine, "DAO.Fields") => _relation = relation;

    public IReadOnlyList<NewRelationFieldObject> Members => _members;

    public override bool Invalid => _relation.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewRelationFieldsObject, CallResult> op)
    {
        var c = (NewRelationFieldsObject)self;
        return c.Invalid ? c.Fail(3420, DaoErrors.ObjectInvalid) : op(c);
    }

    private CallResult Append(nint arg)
    {
        if (Args.ObjectOf<NewRelationFieldObject>(arg) is not { } field)
        {
            return CallResult.Fail(Failure.NotImplemented("Relation.Fields.Append of an argument that is not a CalcDb draft Relation Field"));
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
        foreach (NewRelationFieldObject f in _members)
        {
            f.Release();
        }
        _members.Clear();
    }
}

/// <summary>A draft Field created by <c>Relation.CreateField(Name)</c> (holds <c>Name</c> and <c>ForeignName</c>).</summary>
internal sealed unsafe class NewRelationFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((NewRelationFieldObject)self).Name))
        .Put("Name", (self, args) => { ((NewRelationFieldObject)self).Name = Args.Text(args[^1]) ?? ""; return CallResult.Nothing; })
        .Get("ForeignName", (self, args) => Text(((NewRelationFieldObject)self).ForeignName))
        .Put("ForeignName", (self, args) => { ((NewRelationFieldObject)self).ForeignName = Args.Text(args[^1]) ?? ""; return CallResult.Nothing; });

    public NewRelationFieldObject(NewRelationObject relation, string name) : base(Decl, Vtable, relation.Engine, "DAO.Field")
    {
        Name = name;
        ForeignName = "";
    }

    public string Name { get; private set; }

    public string ForeignName { get; private set; }

    public NewRelationFieldsObject? AppendedTo { get; set; }

    protected override OperationTable Table => Operations;
}
