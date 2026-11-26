using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Format;
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
        .Get("Fields", (self, args) => Live(self, t => Object(t.Fields)))
        .Method("OpenRecordset", static (self, args) => CallResult.Fail(Failure.NotModelled("NotModelled.DraftTableDefOpenRecordset", "TableDef.OpenRecordset on a draft (unappended) TableDef is not recorded")));

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
    /// CreateField(Name, Type, Size) on a draft TableDef (r-dao-1-err-1/draft-tabledef-field,
    /// r-q46-dao-ddl-multi/cf-*): omitted arguments read "", 0 and 0; Type 9999 is 3259 from DAO.Field;
    /// complex types 101..109, dbBigInt (16), and draft dbDecimal (20) are refused by name; the 13 non-Decimal
    /// DAO types (1..12, 15) are accepted with ACE's exact default Size.
    /// </summary>
    private CallResult CreateField(nint name, nint type, nint size) =>
        NewFieldObject.Create(Engine, forAttachedTable: false, isJet4: _database.Profile.Format == FileFormat.Jet4, name, type, size);

    /// <summary>The typed request this draft appends as: its fields in Append order, by the DAO route.</summary>
    public CreateTableRequest? Request(out string? notWritten)
    {
        notWritten = null;
        List<NewField> fields = [];
        foreach (NewFieldObject f in Fields.Members)
        {
            if (f.ToNewField(forAttachedTable: false, out string? fieldNotWritten) is not { } field)
            {
                notWritten = fieldNotWritten;
                return null;
            }
            fields.Add(field);
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
        if (field.AppendedTo is not null || field.IsAttached)
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
/// A Field made by a draft or attached TableDef's CreateField (r-dao-1-err-1/draft-tabledef-field,
/// r-q46-dao-ddl-multi/cf-*, r-dao-1b/coll-append-field-existing-rows). Before Append, it exposes Name, Type, Size,
/// and Attributes (with ACE's exact default Size per DAO type; attached dbDecimal(20) exposes Type=16, Size=8,
/// Attributes=0 before Append) and accepts puts for Type (dbLong/dbText), Attributes (dbAutoIncrField=16), Required,
/// AllowZeroLength, DefaultValue, ValidationRule, and ValidationText. Once appended to an attached TableDef's Fields
/// collection, it binds to the live column and keeps its COM identity (coll-append-field-existing-rows: u6).
/// </summary>
internal sealed unsafe class NewFieldObject : DaoObject
{
    public const short DbBoolean = 1;
    public const short DbByte = 2;
    public const short DbInteger = 3;
    public const short DbLong = 4;
    public const short DbCurrency = 5;
    public const short DbSingle = 6;
    public const short DbDouble = 7;
    public const short DbDate = 8;
    public const short DbBinary = 9;
    public const short DbText = 10;
    public const short DbLongBinary = 11;
    public const short DbMemo = 12;
    public const short DbGUID = 15;
    public const short DbBigInt = 16;
    public const short DbDecimal = 20;
    public const short DbDateTimeExtended = 26;

    internal enum DraftExtendedKind : byte
    {
        None,
        BigInt,
        DateTime2,
    }

    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => ForwardOrDraft(self, "Name", InvokeKind.PropertyGet, args, f => Text(f.Name)))
        .Get("Type", (self, args) => ForwardOrDraft(self, "Type", InvokeKind.PropertyGet, args, f => Int16(f.Type)))
        .Get("Size", (self, args) => ForwardOrDraft(self, "Size", InvokeKind.PropertyGet, args, f => Int32(f.Size)))
        .Get("Attributes", (self, args) => ForwardOrDraft(self, "Attributes", InvokeKind.PropertyGet, args, f => Int32(f._attributes)))
        .Get("OrdinalPosition", (self, args) => ForwardOrDraft(self, "OrdinalPosition", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldOrdinalPosition", "Field.OrdinalPosition on an unappended draft Field is not recorded"))))
        .Get("CollectionIndex", (self, args) => ForwardOrDraft(self, "CollectionIndex", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldCollectionIndex", "Field.CollectionIndex on an unappended draft Field is not recorded"))))
        .Get("Required", (self, args) => ForwardOrDraft(self, "Required", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldRequiredGet", "Field.Required get on an unappended draft Field is not recorded"))))
        .Get("AllowZeroLength", (self, args) => ForwardOrDraft(self, "AllowZeroLength", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldAllowZeroLengthGet", "Field.AllowZeroLength get on an unappended draft Field is not recorded"))))
        .Get("CollatingOrder", (self, args) => ForwardOrDraft(self, "CollatingOrder", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldCollatingOrder", "Field.CollatingOrder on an unappended draft Field is not recorded"))))
        .Get("SourceTable", (self, args) => ForwardOrDraft(self, "SourceTable", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldSourceTable", "Field.SourceTable on an unappended draft Field is not recorded"))))
        .Get("SourceField", (self, args) => ForwardOrDraft(self, "SourceField", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldSourceField", "Field.SourceField on an unappended draft Field is not recorded"))))
        .Get("DefaultValue", (self, args) => ForwardOrDraft(self, "DefaultValue", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldDefaultValueGet", "Field.DefaultValue get on an unappended draft Field is not recorded"))))
        .Get("ValidationRule", (self, args) => ForwardOrDraft(self, "ValidationRule", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldValidationRuleGet", "Field.ValidationRule get on an unappended draft Field is not recorded"))))
        .Get("ValidationText", (self, args) => ForwardOrDraft(self, "ValidationText", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldValidationTextGet", "Field.ValidationText get on an unappended draft Field is not recorded"))))
        .Get("AppendOnly", (self, args) => ForwardOrDraft(self, "AppendOnly", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldAppendOnly", "Field.AppendOnly on an unappended draft Field is not recorded"))))
        .Get("Expression", (self, args) => ForwardOrDraft(self, "Expression", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldExpression", "Field.Expression on an unappended draft Field is not recorded"))))
        .Get("Scale", (self, args) => ForwardOrDraft(self, "Scale", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldScale", "Field.Scale on an unappended draft Field is not recorded"))))
        .Get("DataUpdatable", (self, args) => ForwardOrDraft(self, "DataUpdatable", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldDataUpdatable", "Field.DataUpdatable on an unappended draft Field is not recorded"))))
        .Get("Value", (self, args) => ForwardOrDraft(self, "Value", InvokeKind.PropertyGet, args, f => f.Fail(3219, DaoErrors.InvalidOperation, publishesErrorInfo: true)))
        .Get("ValidateOnSet", (self, args) => ForwardOrDraft(self, "ValidateOnSet", InvokeKind.PropertyGet, args, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ForeignName", (self, args) => ForwardOrDraft(self, "ForeignName", InvokeKind.PropertyGet, args, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ComplexType", (self, args) => ForwardOrDraft(self, "ComplexType", InvokeKind.PropertyGet, args, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("FieldSize", (self, args) => ForwardOrDraft(self, "FieldSize", InvokeKind.PropertyGet, args, f => f.Fail(3267, DaoErrors.RecordsetFieldOnly)))
        .Get("OriginalValue", (self, args) => ForwardOrDraft(self, "OriginalValue", InvokeKind.PropertyGet, args, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("VisibleValue", (self, args) => ForwardOrDraft(self, "VisibleValue", InvokeKind.PropertyGet, args, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Properties", (self, args) => ForwardOrDraft(self, "Properties", InvokeKind.PropertyGet, args, _ => CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperties", "Field.Properties on an unappended draft Field is not recorded"))))
        .Put("Type", (self, args) => ((NewFieldObject)self).SetType(args[^1]))
        .Put("Attributes", (self, args) => ((NewFieldObject)self).SetAttributes(args[^1]))
        .Put("DefaultValue", (self, args) => ((NewFieldObject)self).Keep("DefaultValue", args[^1], v => Args.TypeOf(v) == VarType.Bstr))
        .Put("ValidationRule", (self, args) => { nint val = args[^1]; return ForwardOrDraft(self, "ValidationRule", InvokeKind.PropertyPut, args, f => f.Keep("ValidationRule", val, v => Args.TypeOf(v) == VarType.Bstr)); })
        .Put("ValidationText", (self, args) => { nint val = args[^1]; return ForwardOrDraft(self, "ValidationText", InvokeKind.PropertyPut, args, f => f.Keep("ValidationText", val, v => Args.TypeOf(v) == VarType.Bstr)); })
        .Put("Required", (self, args) => ((NewFieldObject)self).SetRequired(args[^1]))
        .Put("AllowZeroLength", (self, args) => { nint val = args[^1]; return ForwardOrDraft(self, "AllowZeroLength", InvokeKind.PropertyPut, args, f => f.SetAllowZeroLength(val)); });

    private int _attributes;
    private DefinitionCollection? _attachedOwner;
    private DefinitionFieldObject? _attachedField;

    /// <summary>The draft Fields this field was appended to, or null.</summary>
    public NewFieldsObject? AppendedTo { get; set; }

    /// <summary>True once this field has been appended to an attached TableDef's Fields collection.</summary>
    public bool IsAttached => _attachedField is not null;

    /// <summary>True when created via attached <c>TableDef.CreateField(name, 20)</c> (dbDecimal).</summary>
    public bool IsAttachedDecimal { get; }

    /// <summary>Distinguishes draft <c>dbBigInt</c> (16 -> draft Type=19) and <c>dbDateTimeExtended</c> (26 -> draft Type=20).</summary>
    internal DraftExtendedKind ExtendedKind { get; }

    /// <summary>Explicit <c>Required</c> put on the draft field, or null if unset.</summary>
    public bool? RequiredPut { get; private set; }

    /// <summary>Explicit <c>AllowZeroLength</c> put on the draft field, or null if unset.</summary>
    public bool? AllowZeroLengthPut { get; private set; }

    /// <summary>Other property puts kept for Append (DefaultValue, ValidationRule, ValidationText), by name.</summary>
    public Dictionary<string, string> PropertiesPut { get; } = [];

    public override bool Invalid => _attachedOwner is { } owner && owner.Database.Closed;

    private static CallResult ForwardOrDraft(ComObject self, string memberName, InvokeKind kind, ReadOnlySpan<nint> args, Func<NewFieldObject, CallResult> draftOp)
    {
        var f = (NewFieldObject)self;
        if (f._attachedField is { } attached)
        {
            return attached.Run(Decl.Member(memberName, kind), args);
        }
        return f.Invalid ? f.Fail(3420, DaoErrors.ObjectInvalid) : draftOp(f);
    }

    protected override Failure? ScalarArgumentRefusal(MemberDecl member, int parameter, VarType sourceType)
    {
        if (!IsAttached || member.Kind != InvokeKind.PropertyPut)
        {
            return null;
        }
        if (member.Name == "AllowZeroLength")
        {
            return sourceType != VarType.Bool
                ? Failure.NotImplemented($"Field.AllowZeroLength put of variant type {sourceType} (not recorded)")
                : null;
        }
        if (member.Name is "ValidationRule" or "ValidationText")
        {
            if (sourceType != VarType.Bstr && Invalid)
            {
                return Failure.NotImplemented($"Field.{member.Name} put of variant type {sourceType} on a closed Field (qualified.md r-q37 Open)");
            }
            return sourceType is not (VarType.Bstr or VarType.Null)
                ? Failure.NotImplemented($"Field.{member.Name} put of variant type {sourceType} (qualified.md r-q37 Open)")
                : null;
        }
        return null;
    }

    /// <summary>
    /// Shared factory for draft (<c>NewTableDefObject.CreateField</c>) and attached (<c>TableDefObject.CreateField</c>)
    /// fields (r-dao-1-err-1/draft-tabledef-field, r-q46-dao-ddl-multi/cf-*).
    /// </summary>
    public static CallResult Create(EngineState engine, bool forAttachedTable, bool isJet4, nint name, nint type, nint size)
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
        if (t is >= 101 and <= 109)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DaoCreateFieldComplexType", $"TableDef.CreateField of complex DAO type {t} (101..109) is not modelled"));
        }
        if (t == DbBigInt)
        {
            if (forAttachedTable)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoAttachedCreateFieldBigInt", "attached TableDef.CreateField of dbBigInt (16) is not recorded (only draft CreateField is recorded in evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb)"));
            }
            if (isJet4)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoCreateFieldBigIntMdb", "draft TableDef.CreateField of dbBigInt (16) on Jet 4 MDB is not recorded (only ACCDB is recorded in evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb)"));
            }
            // evidence/r-q47-bigint-expr-edges/bi-dao-createfield-addnew-accdb:
            // draft CreateField("B", 16) returns a draft Field with Type=19, Size=8, Attributes=0 before Append.
            var biField = new NewFieldObject(engine, fieldName, type: 19, size: 8, isAttachedDecimal: false, extendedKind: DraftExtendedKind.BigInt);
            CallResult biResult = Object(biField);
            biField.Release();
            return biResult;
        }
        if (t == DbDateTimeExtended)
        {
            if (forAttachedTable)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoAttachedCreateFieldDateTime2", "attached TableDef.CreateField of dbDateTimeExtended (26) is not recorded (only draft CreateField is recorded in evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb)"));
            }
            if (isJet4)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoCreateFieldDateTime2Mdb", "draft TableDef.CreateField of dbDateTimeExtended (26) on Jet 4 MDB is not recorded (only ACCDB is recorded in evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb)"));
            }
            // evidence/r-q49-dt2-draftprops/dt2-dao-createfield-addnew-accdb:
            // draft CreateField("D", 26) returns a draft Field with Type=20, Size=42, Attributes=0 before Append.
            var dt2Field = new NewFieldObject(engine, fieldName, type: 20, size: 42, isAttachedDecimal: false, extendedKind: DraftExtendedKind.DateTime2);
            CallResult dt2Result = Object(dt2Field);
            dt2Field.Release();
            return dt2Result;
        }
        if (t == DbDecimal)
        {
            if (!forAttachedTable)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoCreateFieldDraftDecimal", "draft TableDef.CreateField of dbDecimal (20) is not recorded (only attached TableDef.CreateField(..., 20) is recorded in evidence/r-q46-dao-ddl-multi/cf-attached-decimal-*)"));
            }
            // evidence/r-q46-dao-ddl-multi/cf-attached-decimal-accdb, cf-attached-decimal-mdb:
            // attached CreateField("FDec", 20) returns a draft Field with Type=16, Size=8, Attributes=0 before Append.
            var decField = new NewFieldObject(engine, fieldName, type: DbBigInt, size: 8, isAttachedDecimal: true);
            CallResult decResult = Object(decField);
            decField.Release();
            return decResult;
        }
        bool sizeOmitted = size == 0 || TypedCall.IsMissing(Args.Cell(size));
        int resolvedSize;
        switch (t)
        {
            case 0:
                resolvedSize = s;
                break;
            case DbBoolean:
            case DbByte:
                resolvedSize = 1;
                break;
            case DbInteger:
                resolvedSize = 2;
                break;
            case DbLong:
            case DbSingle:
                resolvedSize = 4;
                break;
            case DbCurrency:
            case DbDouble:
            case DbDate:
                resolvedSize = 8;
                break;
            case DbBinary:
                resolvedSize = sizeOmitted ? 0 : s;
                break;
            case DbText:
                // evidence/r-q46-dao-ddl-multi/cf-text-default-accdb: omitted Size reads 0 on draft, writes 0x01FE (255 chars).
                resolvedSize = sizeOmitted ? 0 : s;
                break;
            case DbLongBinary:
            case DbMemo:
                resolvedSize = 0;
                break;
            case DbGUID:
                resolvedSize = 16;
                break;
            default:
                return CallResult.Fail(Failure.NotModelled("NotModelled.DaoCreateFieldType", $"CreateField of DAO type {t} is not recorded"));
        }
        var field = new NewFieldObject(engine, fieldName, (short)t, resolvedSize, isAttachedDecimal: false);
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public NewField? ToNewField(bool forAttachedTable, out string? notWritten)
    {
        notWritten = null;
        if (PropertiesPut.Count > 0)
        {
            notWritten = $"{(forAttachedTable ? "Fields.Append" : "TableDefs.Append")} of a field with {string.Join(", ", PropertiesPut.Keys)} put (not written yet: W-07/native-metadata-write)";
            return null;
        }
        bool isAutoIncr = (_attributes & 16) != 0;
        if (!forAttachedTable)
        {
            if (RequiredPut == true || AllowZeroLengthPut == true)
            {
                notWritten = "TableDefs.Append of a draft field with Required=True or AllowZeroLength=True is not recorded (only attached Fields.Append records Required=True and AllowZeroLength=True in cf-attached-scalar-*)";
                return null;
            }
            if (isAutoIncr && Type != DbLong)
            {
                notWritten = $"TableDefs.Append of a field of DAO type {Type} with dbAutoIncrField (16) is not recorded";
                return null;
            }
        }
        else
        {
            if (isAutoIncr)
            {
                notWritten = "attached TableDef.Fields.Append of an AutoNumber (dbAutoIncrField) field is not recorded";
                return null;
            }
            if (IsAttachedDecimal)
            {
                if (AllowZeroLengthPut == true)
                {
                    notWritten = "attached TableDef.Fields.Append of dbDecimal with AllowZeroLength=True is not recorded";
                    return null;
                }
                return new NewField(Name, NewFieldType.Decimal, Precision: 38, Scale: 0, Required: RequiredPut ?? false);
            }
        }

        bool required = RequiredPut ?? false;
        bool azl = AllowZeroLengthPut ?? false;
        if (ExtendedKind == DraftExtendedKind.BigInt)
        {
            if (forAttachedTable)
            {
                notWritten = "attached TableDef.Fields.Append of dbBigInt is not recorded";
                return null;
            }
            return new NewField(Name, NewFieldType.BigInt, Required: required, AllowZeroLength: azl);
        }
        if (ExtendedKind == DraftExtendedKind.DateTime2)
        {
            if (forAttachedTable)
            {
                notWritten = "attached TableDef.Fields.Append of dbDateTimeExtended is not recorded";
                return null;
            }
            return new NewField(Name, NewFieldType.DateTime2, Required: required, AllowZeroLength: azl);
        }
        switch (Type)
        {
            case DbBoolean:
                return new NewField(Name, NewFieldType.YesNo, Required: required, AllowZeroLength: azl);
            case DbByte:
                return new NewField(Name, NewFieldType.Byte, Required: required, AllowZeroLength: azl);
            case DbInteger:
                return new NewField(Name, NewFieldType.Short, Required: required, AllowZeroLength: azl);
            case DbLong:
                return new NewField(Name, isAutoIncr ? NewFieldType.Counter : NewFieldType.Long, Required: required, AllowZeroLength: azl);
            case DbCurrency:
                return new NewField(Name, NewFieldType.Currency, Required: required, AllowZeroLength: azl);
            case DbSingle:
                return new NewField(Name, NewFieldType.Single, Required: required, AllowZeroLength: azl);
            case DbDouble:
                return new NewField(Name, NewFieldType.Double, Required: required, AllowZeroLength: azl);
            case DbDate:
                return new NewField(Name, NewFieldType.DateTime, Required: required, AllowZeroLength: azl);
            case DbBinary:
                return new NewField(Name, NewFieldType.VarBinary, Size: Size, Required: required, AllowZeroLength: azl);
            case DbText:
                return new NewField(Name, NewFieldType.Text, Size: Size == 0 ? 255 : Size, Required: required, AllowZeroLength: azl);
            case DbLongBinary:
                return new NewField(Name, NewFieldType.Ole, Required: required, AllowZeroLength: azl);
            case DbMemo:
                return new NewField(Name, NewFieldType.Memo, Required: required, AllowZeroLength: azl);
            case DbGUID:
                return new NewField(Name, NewFieldType.Guid, Required: required, AllowZeroLength: azl);
            default:
                notWritten = $"{(forAttachedTable ? "Fields.Append" : "TableDefs.Append")} of a field of DAO type {Type}";
                return null;
        }
    }

    /// <summary>Bind this Field to its persisted column after an attached <c>TableDef.Fields.Append</c> succeeds.</summary>
    public void Attach(TableDefObject table, DefinitionCollection owner, ColumnDescriptor column, int ordinal)
    {
        _attachedOwner = owner;
        _attachedField = new DefinitionFieldObject(owner, table, column, indexColumn: null, ordinal);
    }

    private CallResult SetType(nint value)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.Int32(value, null) is not (DbLong or DbText) and var type)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldType", $"a draft field's Type put of {type?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "a value that is not a number"} (dbText is recorded)"));
        }
        Type = (short)Args.Int32(value, null)!.Value;
        return CallResult.Nothing;
    }

    private CallResult SetAttributes(nint value)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.Int32(value, null) != 16)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperty", $"a draft field's Attributes put of a value other than dbAutoIncrField (16) is not recorded"));
        }
        _attributes = 16;
        return CallResult.Nothing;
    }

    private CallResult SetRequired(nint value)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.TypeOf(value) != VarType.Bool)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperty", $"a draft field's Required put of a {Args.TypeOf(value)} value is not recorded"));
        }
        RequiredPut = Args.Bool(value, omitted: false)!.Value;
        return CallResult.Nothing;
    }

    private CallResult SetAllowZeroLength(nint value)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (Args.TypeOf(value) != VarType.Bool)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperty", $"a draft field's AllowZeroLength put of a {Args.TypeOf(value)} value is not recorded"));
        }
        AllowZeroLengthPut = Args.Bool(value, omitted: false)!.Value;
        return CallResult.Nothing;
    }

    private CallResult Keep(string name, nint value, Func<nint, bool> recorded)
    {
        if (IsAttached)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!recorded(value))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.DraftFieldProperty", $"a draft field's {name} put of a {Args.TypeOf(value)} value or one not recorded (r-cat-1b/create-props)"));
        }
        PropertiesPut[name] = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    public NewFieldObject(EngineState engine, string name, short type, int size, bool isAttachedDecimal = false, DraftExtendedKind extendedKind = DraftExtendedKind.None)
        : base(Decl, Vtable, engine, "DAO.Field")
    {
        Name = name;
        Type = type;
        Size = size;
        IsAttachedDecimal = isAttachedDecimal;
        ExtendedKind = extendedKind;
    }

    public string Name { get; }

    public short Type { get; private set; }

    public int Size { get; }

    protected override OperationTable Table => Operations;

    protected override void OnFinalRelease()
    {
        if (_attachedOwner is not null)
        {
            _attachedOwner.Released(this);
            _attachedOwner = null;
        }
        if (_attachedField is not null)
        {
            _attachedField.Release();
            _attachedField = null;
        }
    }
}
