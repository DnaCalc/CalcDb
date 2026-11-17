using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Windows;
using CalcDb.Format;
using CalcDb.Isam;

namespace CalcDb.Com.Dao;

/// <summary>
/// A table-type Recordset (Recordset2) over the native table cursor (W-04/table-cursor): storage order, the qualified
/// movement rules (R-CURSOR-1 CUR-POS), the exact count at open (CUR-POP) and root-locator bookmarks (CUR-ID).
/// After Close every member fails 3420 (R-ACT-1 ACT-RETAIN), with source DAO.Recordset.
/// </summary>
internal sealed unsafe class RecordsetObject : DaoObject
{
    /// <summary>dbOpenTable.</summary>
    private const int TableType = 1;

    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(TableType)))
        .Get("RecordCount", (self, args) => Live(self, r => Int32((int)r.Cursor.RecordCount)))
        .Get("EOF", (self, args) => Live(self, r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Live(self, r => Bool(r.Cursor.Bof)))
        .Get("NoMatch", (self, args) => Live(self, r => Bool(r.Cursor.NoMatch)))
        .Get("Name", (self, args) => Live(self, r => Text(r.TableName)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Live(self, r => r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Live(self, r => r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Move(rows, start)); })
        .Get("Bookmark", (self, args) => Live(self, r => r.GetBookmark()))
        .Put("Bookmark", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetBookmark(value)); })
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    private FieldsObject? _fields;

    public RecordsetObject(DatabaseObject database, string tableName, TableCursor cursor) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        Database = database;
        TableName = tableName;
        Cursor = cursor;
        database.AddRef(); // a recordset keeps its Database object alive
    }

    public DatabaseObject Database { get; }

    public string TableName { get; }

    public TableCursor Cursor { get; }

    public bool Closed { get; private set; }

    protected override OperationTable Table => Operations;

    private FieldsObject FieldsCollection => _fields ??= new FieldsObject(this);

    private static CallResult Live(ComObject self, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    /// <summary>The movement outcome as DAO publishes it: NoCurrentRecord is 3021 (CUR-POS).</summary>
    private CallResult Moved(MoveOutcome outcome) => outcome switch
    {
        MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
        MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
        _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
    };

    private CallResult Move(nint rowsArg, nint startBookmark)
    {
        if (startBookmark != 0)
        {
            return CallResult.Fail(Failure.NotImplemented("Move with a StartBookmark"));
        }
        int? rows = Args.Int32(rowsArg, omitted: null);
        return rows is null ? CallResult.Fail(Failure.NotImplemented("Move with a Rows argument that is not a number")) : Moved(Cursor.Move(rows.Value));
    }

    private CallResult GetBookmark()
    {
        byte[]? bookmark = Cursor.GetBookmark(out CursorFailure? failure);
        if (bookmark is null)
        {
            return failure == CursorFailure.NoCurrentRecord ? Fail(3021, DaoErrors.NoCurrentRecord) : CallResult.Fail(Failure.NotImplemented("Bookmark: " + failure));
        }
        nint array = SafeArrays.Create(VarType.UI1, (0, bookmark.Length));
        bookmark.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bookmark.Length));
        return CallResult.Of(OwnedVariant.TakeArray(array, VarType.UI1));
    }

    private CallResult SetBookmark(nint value)
    {
        NativeVariant* v = Args.Cell(value);
        if (v->Vt != (ushort)(VarType.Array | VarType.UI1) || v->Pointer == 0 || SafeArrays.ElementType(v->Pointer) != VarType.UI1)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark put of a value that is not a Byte array"));
        }
        nint psa = v->Pointer;
        (int Lower, int Count)[] shape = SafeArrays.Shape(psa);
        if (shape.Length != 1)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark put of a multi-dimensional array"));
        }
        byte[] bytes = shape[0].Count <= 0 ? [] : new Span<byte>(SafeArrays.ElementPointer(psa, shape[0].Lower), shape[0].Count).ToArray();
        CursorFailure? failure = Cursor.SetBookmark(bytes);
        return failure switch
        {
            null => CallResult.Nothing,
            CursorFailure.InvalidBookmark => Fail(3159, "Not a valid bookmark."),
            _ => CallResult.Fail(Failure.NotImplemented("Bookmark put: " + failure)),
        };
    }

    /// <summary>Close: the cursor's pins and bound row are released; the Database forgets this recordset.</summary>
    private CallResult Close()
    {
        CloseCursor();
        Database.Forget(this);
        return CallResult.Nothing;
    }

    public void CloseCursor()
    {
        if (!Closed)
        {
            Cursor.Dispose();
            Closed = true;
        }
    }

    public void FieldsReleased() => _fields = null;

    protected override void OnFinalRelease()
    {
        if (!Closed)
        {
            Close();
        }
        _fields?.Release();
        Database.Release();
    }
}

/// <summary>
/// Fields of a recordset: one collection object per recordset, kept by it (ACE's Release of a held Fields returns 2).
/// A Field wrapper is shared while a client holds it (R-DAO-1 DAO-ID) and made anew after its last release. Lookup
/// follows DAO-COLL. After the recordset closes, members fail 3420 with source DAO.Recordset, as ACE reports it.
/// </summary>
internal sealed unsafe class FieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, f => Int16(f.Recordset.Cursor.Table.Columns.Count)))
        .Get("Item", (self, args) => { nint index = args[0]; return Live(self, f => f.Item(index)); })
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private readonly Dictionary<int, FieldObject> _held = [];

    public FieldsObject(RecordsetObject recordset) : base(Decl, Vtable, recordset.Engine, "DAO.Fields")
    {
        Recordset = recordset;
    }

    public RecordsetObject Recordset { get; }

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<FieldsObject, CallResult> op)
    {
        var f = (FieldsObject)self;
        return f.Recordset.Closed ? f.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    private CallResult Item(nint index)
    {
        IReadOnlyList<ColumnDescriptor> columns = Recordset.Cursor.Table.Columns;
        int? ordinal = CollectionLookup.Find(index, columns.Count, i => columns[i].Name, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Item with an object index (ACE: 3001 and 3421)"));
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        if (_held.TryGetValue(i, out FieldObject? held))
        {
            return Object(held);
        }
        var field = new FieldObject(this, i);
        _held[i] = field;
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public void Released(int ordinal) => _held.Remove(ordinal);
}

/// <summary>
/// A recordset Field (Field3): its column, read from the recordset's current row on each Value get (a held Field
/// follows the current row, R-DAO-1 DAO-ID). Value projects the native value as R-ORACLE-1 FMT1-FAMILIES observed.
/// Value failures publish an IErrorInfo (R-ERR-1 ERR-EXCEP); other members' do not.
/// </summary>
internal sealed unsafe class FieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.DaoType() is int t ? Int16(t) : CallResult.Fail(Failure.NotImplemented($"Field.Type of native type {f.Column.TypeCode}"))))
        .Get("Size", (self, args) => Live(self, f => f.Size() is int s ? Int32(s) : CallResult.Fail(Failure.NotImplemented($"Field.Size of native type {f.Column.TypeCode}"))))
        .Get("OrdinalPosition", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("Value", (self, args) => ((FieldObject)self).Value());

    private readonly FieldsObject _fields;
    private readonly int _ordinal;

    public FieldObject(FieldsObject fields, int ordinal) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        fields.AddRef();
    }

    private RecordsetObject Recordset => _fields.Recordset;

    private ColumnDescriptor Column => Recordset.Cursor.Table.Columns[_ordinal];

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<FieldObject, CallResult> op)
    {
        var f = (FieldObject)self;
        return f.Recordset.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid) : op(f);
    }

    /// <summary>DataTypeEnum: the native codes 1-12 and 15 are DAO's own numbers (dbBoolean … dbMemo, dbGUID).</summary>
    private int? DaoType() => Column.TypeCode is >= 1 and <= 12 or 15 ? Column.TypeCode : null;

    /// <summary>Size: Text in characters (the column length in bytes, halved), Memo and OLE 0, fixed types their length.</summary>
    private int? Size() => Column.TypeCode switch
    {
        NativeTypes.Text => Column.Length / 2,
        NativeTypes.Ole or NativeTypes.Memo => 0,
        >= 1 and <= 9 or 15 => Column.Length,
        _ => null,
    };

    private CallResult Value()
    {
        if (Recordset.Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true);
        }
        FieldResult read = Recordset.Cursor.Read(Column);
        if (read.Failure == CursorFailure.NoCurrentRecord)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord, publishesErrorInfo: true);
        }
        if (!read.Succeeded)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Value when the row cannot be read: {read.Failure} {read.Detail}"));
        }
        return Project(read.Value!) is { } value ? CallResult.Of(value) : CallResult.Fail(Failure.NotImplemented($"Field.Value of a {read.Value!.Kind} value"));
    }

    /// <summary>
    /// The VARIANT of a native value (R-ORACLE-1 FMT1-FAMILIES): Byte UI1, Integer I2, Long I4, Single R4, Double R8,
    /// Currency CY, Date DATE, Text and Memo BSTR, Yes/No BOOL, Binary and OLE a UI1 array (an empty one reads Null),
    /// GUID the BSTR "{guid {…}}".
    /// </summary>
    private static OwnedVariant? Project(NativeValue v) => v.Kind switch
    {
        NativeKind.Null => OwnedVariant.Null(),
        NativeKind.Boolean => OwnedVariant.FromBoolean(v.Boolean),
        NativeKind.Byte => OwnedVariant.FromByte((byte)v.Integer),
        NativeKind.Integer => OwnedVariant.FromInt16((short)v.Integer),
        NativeKind.Long => OwnedVariant.FromInt32((int)v.Integer),
        NativeKind.Currency => OwnedVariant.FromCurrency(v.Integer),
        NativeKind.Single => OwnedVariant.FromSingle(BitConverter.Int32BitsToSingle((int)(uint)v.Bits)),
        NativeKind.Double => OwnedVariant.FromDouble(BitConverter.Int64BitsToDouble((long)v.Bits)),
        NativeKind.DateTime => OwnedVariant.FromDate(BitConverter.Int64BitsToDouble((long)v.Bits)),
        NativeKind.Text => OwnedVariant.FromText(v.Text!),
        NativeKind.Binary or NativeKind.LongBinary => v.Bytes is { Length: > 0 } bytes ? ByteArray(bytes) : OwnedVariant.Null(),
        NativeKind.Guid => OwnedVariant.FromText("{guid " + new Guid(v.Bytes!).ToString("B").ToUpperInvariant() + "}"),
        _ => null,
    };

    private static OwnedVariant ByteArray(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return OwnedVariant.TakeArray(array, VarType.UI1);
    }

    protected override void OnFinalRelease()
    {
        _fields.Released(_ordinal);
        _fields.Release();
    }
}

/// <summary>DBEngine.Errors: the engine state's latest failure (R-ORACLE-1 OR-OBS), one collection object per engine.</summary>
internal sealed unsafe class ErrorsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Errors;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int16(((ErrorsObject)self).Engine.Errors.Count))
        .Get("Item", (self, args) => ((ErrorsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => CallResult.Nothing);

    public ErrorsObject(EngineState engine) : base(Decl, Vtable, engine, "DAO.Errors")
    {
    }

    protected override OperationTable Table => Operations;

    private CallResult Item(nint index)
    {
        List<Failure> errors = Engine.Errors;
        int? ordinal = CollectionLookup.Find(index, errors.Count, i => errors[i].Number.ToString(System.Globalization.CultureInfo.InvariantCulture), out bool objectIndex);
        if (objectIndex || ordinal is not int i)
        {
            return objectIndex ? CallResult.Fail(Failure.NotImplemented("Errors.Item with an object index")) : Fail(3265, DaoErrors.ItemNotFound);
        }
        var error = new ErrorObject(Engine, errors[i]);
        CallResult result = Object(error);
        error.Release();
        return result;
    }
}

/// <summary>An Error: a recorded failure's number (the scode's low word), text, source and help (R-ERR-1 ERR-EXCEP).</summary>
internal sealed unsafe class ErrorObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Error;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Number", (self, args) => Int32(((ErrorObject)self)._failure.Number))
        .Get("Description", (self, args) => Text(((ErrorObject)self)._failure.Description))
        .Get("Source", (self, args) => Text(((ErrorObject)self)._failure.Source))
        .Get("HelpContext", (self, args) => Int32(((ErrorObject)self)._failure.HelpContext))
        .Get("HelpFile", (self, args) => Text(((ErrorObject)self)._failure.HelpFile ?? ""));

    private readonly Failure _failure;

    public ErrorObject(EngineState engine, Failure failure) : base(Decl, Vtable, engine, "DAO.Error")
    {
        _failure = failure;
    }

    protected override OperationTable Table => Operations;
}