using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;

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
        // A draft's Name put is kept and read back (draft-tabledef-field: Name = "D1x", then "D1x"); a name is checked at Append.
        .Put("Name", (self, args) => { nint value = args[^1]; return Live(self, t => { t.Name = Args.Text(value) ?? ""; return CallResult.Nothing; }); })
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

    public string Name { get; private set; }

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
            if (f.PropertiesPut.Count > 0)
            {
                notWritten = $"TableDefs.Append of a field with {string.Join(", ", f.PropertiesPut.Keys)} put (the schema writer writes no field properties or attributes yet: W-07/native-metadata-write)";
                return null;
            }
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

    /// <summary>
    /// Append: 3367 when the field is in a draft's Fields already, this one or another draft's (draft-tabledef-field:
    /// appending u15 again to its own draft, and to the draft D2).
    /// </summary>
    private CallResult Append(nint arg)
    {
        if (Args.ObjectOf<NewFieldObject>(arg) is not { } field)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Append of an argument that is not a CalcDb draft Field"));
        }
        if (field.AppendedTo is not null)
        {
            return Fail(3367, DaoErrors.AlreadyInCollection);
        }
        field.AddRef(); // the collection's reference
        field.AppendedTo = this;
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

/// <summary>
/// A Field made by a draft TableDef's CreateField (r-dao-1-err-1/draft-tabledef-field: Name, Type and Size as given;
/// omitted "", 0, 0). Before the TableDef is appended, a Type put of dbLong or dbText is kept (draft-tabledef-field: Type =
/// 10 on an appended draft field), and the puts r-cat-1b/create-props makes are accepted and kept for Append: Attributes
/// dbAutoIncrField (16), DefaultValue, Required, AllowZeroLength, ValidationRule and ValidationText. Their reads before
/// Append and other Attributes and Type values are not recorded.
/// </summary>
internal sealed unsafe class NewFieldObject : DaoObject
{
    public const short DbLong = 4;
    public const short DbText = 10;

    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Text(((NewFieldObject)self).Name))
        .Get("Type", (self, args) => Int16(((NewFieldObject)self).Type))
        .Get("Size", (self, args) => Int32(((NewFieldObject)self).Size))
        .Put("Type", (self, args) => ((NewFieldObject)self).SetType(args[^1]))
        .Put("Attributes", (self, args) => ((NewFieldObject)self).Keep("Attributes", args[^1], v => Args.Int32(v, null) == 16))
        .Put("DefaultValue", (self, args) => ((NewFieldObject)self).Keep("DefaultValue", args[^1], v => Args.TypeOf(v) == VarType.Bstr))
        .Put("ValidationRule", (self, args) => ((NewFieldObject)self).Keep("ValidationRule", args[^1], v => Args.TypeOf(v) == VarType.Bstr))
        .Put("ValidationText", (self, args) => ((NewFieldObject)self).Keep("ValidationText", args[^1], v => Args.TypeOf(v) == VarType.Bstr))
        .Put("Required", (self, args) => ((NewFieldObject)self).Keep("Required", args[^1], v => Args.TypeOf(v) == VarType.Bool))
        .Put("AllowZeroLength", (self, args) => ((NewFieldObject)self).Keep("AllowZeroLength", args[^1], v => Args.TypeOf(v) == VarType.Bool));

    /// <summary>The draft Fields this field was appended to, or null.</summary>
    public NewFieldsObject? AppendedTo { get; set; }

    /// <summary>The property puts kept for Append, by name, as their text.</summary>
    public Dictionary<string, string> PropertiesPut { get; } = [];

    private CallResult SetType(nint value)
    {
        if (Args.Int32(value, null) is not (DbLong or DbText) and var type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldType", $"a draft field's Type put of {type?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a value that is not a number"} (dbText is recorded)"));
        }
        Type = (short)Args.Int32(value, null)!.Value;
        return CallResult.Nothing;
    }

    private CallResult Keep(string name, nint value, Func<nint, bool> recorded)
    {
        if (!recorded(value))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperty", $"a draft field's {name} put of a {Args.TypeOf(value)} value or one not recorded (r-cat-1b/create-props)"));
        }
        PropertiesPut[name] = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    public NewFieldObject(NewTableDefObject table, string name, short type, int size) : base(Decl, Vtable, table.Engine, "DAO.Field")
    {
        Name = name;
        Type = type;
        Size = size;
    }

    public string Name { get; }

    public short Type { get; private set; }

    public int Size { get; }

    protected override OperationTable Table => Operations;
}
