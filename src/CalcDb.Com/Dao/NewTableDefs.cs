using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;

namespace CalcDb.Com.Dao;

/// <summary>
/// A TableDef made by <c>Database.CreateTableDef</c> and not yet appended (II-12 DAO definition objects; W-07/native-metadata-write):
/// its name and the fields appended to it, which <c>TableDefs.Append</c> lowers to a catalog change
/// (<see cref="DatabaseObject.AppendTableDef"/>). Members as recorded in r-dao-1-err-1/draft-tabledef-field (a missing,
/// empty or Empty name reads ""; Null is 3421 at CreateTableDef) and r-q11-ddl-closure/append-dao. The clock reading at
/// CreateTableDef is the new row's DateCreate (ACE's DAO row has DateCreate earlier than DateUpdate, append-dao; which
/// of ACE's calls takes each reading is not recorded, and both differ in every run).
/// </summary>
internal sealed unsafe class NewTableDefObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.TableDef;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, t => Text(t.Name)))
        .Method("CreateField", (self, args) => { nint name = args.Length > 0 ? args[0] : 0, type = args.Length > 1 ? args[1] : 0, size = args.Length > 2 ? args[2] : 0; return Live(self, t => t.CreateField(name, type, size)); })
        .Get("Fields", (self, args) => Live(self, t => Object(t.Fields)));

    private readonly DatabaseObject _database;

    public NewTableDefObject(DatabaseObject database, string name) : base(Decl, Vtable, database.Engine, "DAO.TableDef")
    {
        _database = database;
        Name = name;
        Created = DateTime.Now;
        Fields = new NewFieldsObject(this);
    }

    public string Name { get; }

    public DateTime Created { get; }

    /// <summary>The draft's Fields: one collection object for the draft's life (the collection keeps the draft alive while held).</summary>
    public NewFieldsObject Fields { get; }

    public override bool Invalid => _database.Closed;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewTableDefObject, CallResult> op)
    {
        var t = (NewTableDefObject)self;
        return t._database.Closed ? t.Fail(3420, DaoErrors.ObjectInvalid) : op(t);
    }

    /// <summary>
    /// CreateField(Name, Type, Size) on a draft (r-dao-1-err-1/draft-tabledef-field): omitted arguments read "", 0 and 0;
    /// Type 9999 is 3259 from DAO.Field; dbLong (4) and dbText (10), with any Size, are recorded as accepted.
    /// </summary>
    private CallResult CreateField(nint name, nint type, nint size)
    {
        string fieldName = name == 0 ? "" : Args.Text(name) ?? "";
        int? fieldType = Args.Int32(type, 0);
        int? fieldSize = Args.Int32(size, 0);
        if (fieldType is not int t || fieldSize is not int s)
        {
            return CallResult.Fail(Failure.NotImplemented("CreateField with a Type or Size that does not convert to a number"));
        }
        if (t == 9999)
        {
            return CallResult.Fail(Failure.Dao(3259, "DAO.Field", DaoErrors.InvalidFieldType));
        }
        if (t is not (0 or NewFieldObject.DbLong or NewFieldObject.DbText))
        {
            return CallResult.Fail(Failure.NotImplemented($"CreateField of DAO type {t} (only dbLong, dbText and 9999 are recorded)"));
        }
        var field = new NewFieldObject(this, fieldName, (short)t, s);
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    /// <summary>The typed request this draft appends as: its fields in Append order, by the DAO route.</summary>
    public CreateTableRequest? Request(out string? notWritten)
    {
        notWritten = null;
        List<NewField> fields = [];
        foreach (NewFieldObject f in Fields.Members)
        {
            switch (f.Type)
            {
                case NewFieldObject.DbLong:
                    fields.Add(new NewField(f.Name, NewFieldType.Long));
                    break;
                case NewFieldObject.DbText:
                    fields.Add(new NewField(f.Name, NewFieldType.Text, f.Size));
                    break;
                default:
                    notWritten = $"TableDefs.Append of a field of DAO type {f.Type}";
                    return null;
            }
        }
        return new CreateTableRequest(Name, fields, SchemaRoute.Dao);
    }
}

/// <summary>
/// The Fields of a draft TableDef (r-dao-1-err-1/draft-tabledef-field): Append keeps the field object; appending the same
/// object twice is 3367 from DAO.Fields, while another object of the same name is accepted (the record appends two
/// fields named "A"); Count counts them.
/// </summary>
internal sealed unsafe class NewFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, c => Int16(c._members.Count)))
        .Method("Append", (self, args) => { nint field = args[0]; return Live(self, c => c.Append(field)); })
        .Method("Refresh", (self, args) => Live(self, c => CallResult.Nothing));

    private readonly NewTableDefObject _table;
    private readonly List<NewFieldObject> _members = [];

    public NewFieldsObject(NewTableDefObject table) : base(Decl, Vtable, table.Engine, "DAO.Fields") => _table = table;

    public IReadOnlyList<NewFieldObject> Members => _members;

    public override bool Invalid => _table.Invalid;

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<NewFieldsObject, CallResult> op)
    {
        var c = (NewFieldsObject)self;
        return c.Invalid ? c.Fail(3420, DaoErrors.ObjectInvalid) : op(c);
    }

    private CallResult Append(nint arg)
    {
        if (Args.ObjectOf<NewFieldObject>(arg) is not { } field)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Append of an argument that is not a CalcDb draft Field"));
        }
        if (_members.Contains(field))
        {
            return Fail(3367, DaoErrors.AlreadyInCollection);
        }
        field.AddRef(); // the collection's reference
        _members.Add(field);
        return CallResult.Nothing;
    }

    protected override void OnFinalRelease()
    {
        foreach (NewFieldObject f in _members)
        {
            f.Release();
        }
        _members.Clear();
    }
}

/// <summary>A Field made by a draft TableDef's CreateField (r-dao-1-err-1/draft-tabledef-field: Name, Type and Size as given; omitted "", 0, 0).</summary>
internal sealed unsafe class NewFieldObject : DaoObject
{
    public const short DbLong = 4;
    public const short DbText = 10;

    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((NewFieldObject)self).Name))
        .Get("Type", (self, args) => Int16(((NewFieldObject)self).Type))
        .Get("Size", (self, args) => Int32(((NewFieldObject)self).Size));

    public NewFieldObject(NewTableDefObject table, string name, short type, int size) : base(Decl, Vtable, table.Engine, "DAO.Field")
    {
        Name = name;
        Type = type;
        Size = size;
    }

    public string Name { get; }

    public short Type { get; }

    public int Size { get; }

    protected override OperationTable Table => Operations;
}
