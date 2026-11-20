using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;

namespace CalcDb.Com.Ado;

/// <summary>
/// ADODB.Recordset (_Recordset, dual) as constructed and closed (W-09/construction-policy; II-12 §18). The cursor
/// request (CursorType, LockType, CursorLocation, MaxRecords, Source) is held while closed (r-ado-1b/rs-open-by-properties,
/// conn-cursorlocation-inherit); every member that needs an open result fails 0x800A0E78 (the records' closed-state
/// failures). Open is the named not-implemented outcome: a connection's data path is W-09/native-read-query and a
/// connectionless or persisted local store W-09/events-persistence-inventory.
/// </summary>
internal sealed class RecordsetObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Recordset;
    private static readonly nint Vtable = DualEntries.Build(Decl);

    /// <summary>Members a closed Recordset refuses with 0x800A0E78 in the records (R-ADO-1 ADO-RESULTS, ADO-CURSOR, ADO-EDIT, ADO-BULK).</summary>
    private static readonly (string Name, InvokeKind Kind)[] ClosedRefusals =
    [
        ("BOF", InvokeKind.PropertyGet), ("EOF", InvokeKind.PropertyGet), ("RecordCount", InvokeKind.PropertyGet),
        ("AbsolutePosition", InvokeKind.PropertyGet), ("Bookmark", InvokeKind.PropertyGet), ("EditMode", InvokeKind.PropertyGet),
        ("Status", InvokeKind.PropertyGet), ("MoveFirst", InvokeKind.Method), ("MoveLast", InvokeKind.Method),
        ("MoveNext", InvokeKind.Method), ("MovePrevious", InvokeKind.Method), ("Close", InvokeKind.Method),
        ("AddNew", InvokeKind.Method), ("Update", InvokeKind.Method), ("CancelUpdate", InvokeKind.Method),
        ("UpdateBatch", InvokeKind.Method), ("Clone", InvokeKind.Method), ("GetRows", InvokeKind.Method),
        ("GetString", InvokeKind.Method), ("Find", InvokeKind.Method), ("Save", InvokeKind.Method),
    ];

    private static readonly OperationTable Operations = BuildOperations();

    private static OperationTable BuildOperations()
    {
        var table = new OperationTable(Decl)
            .Get("State", (self, args) => Int32(0))
            .Get("CursorType", (self, args) => Int32(((RecordsetObject)self)._cursorType))
            .Put("CursorType", (self, args) => ((RecordsetObject)self).PutRequest(args[0], 0, 3, ref ((RecordsetObject)self)._cursorType, "CursorType"))
            .Get("LockType", (self, args) => Int32(((RecordsetObject)self)._lockType))
            .Put("LockType", (self, args) => ((RecordsetObject)self).PutRequest(args[0], 1, 4, ref ((RecordsetObject)self)._lockType, "LockType"))
            .Get("CursorLocation", (self, args) => Int32(((RecordsetObject)self)._cursorLocation))
            .Put("CursorLocation", (self, args) => ((RecordsetObject)self).PutRequest(args[0], 2, 3, ref ((RecordsetObject)self)._cursorLocation, "CursorLocation"))
            .Get("MaxRecords", (self, args) => Int32(((RecordsetObject)self)._maxRecords))
            .Put("MaxRecords", (self, args) => ((RecordsetObject)self).PutRequest(args[0], 0, int.MaxValue, ref ((RecordsetObject)self)._maxRecords, "MaxRecords"))
            .Get("Source", (self, args) => ((RecordsetObject)self).GetSource())
            .Put("Source", (self, args) => ((RecordsetObject)self).PutSource(args[0]))
            .Method("Open", (self, args) => NotImplemented(args[1] == 0
                ? "Recordset.Open without a connection (a connectionless or persisted local store: W-09/events-persistence-inventory)"
                : "Recordset.Open over a connection (a data path: W-09/native-read-query)"));
        foreach ((string name, InvokeKind kind) in ClosedRefusals)
        {
            table = kind == InvokeKind.Method
                ? table.Method(name, static (self, args) => ((RecordsetObject)self).Fail(AdoErrors.ObjectClosed))
                : table.Get(name, static (self, args) => ((RecordsetObject)self).Fail(AdoErrors.ObjectClosed));
        }
        return table;
    }

    private int _cursorType; // adOpenForwardOnly
    private int _lockType = 1; // adLockReadOnly
    private int _cursorLocation = 2; // adUseServer
    private int _maxRecords;
    private string? _source;

    private RecordsetObject() : base(Decl, Vtable, "ADODB.Recordset")
    {
    }

    protected override OperationTable Table => Operations;

    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result) => Publish(new RecordsetObject(), outer, iid, result);

    /// <summary>A cursor request on a closed Recordset: held as given within its enum's range (the values the records set).</summary>
    private CallResult PutRequest(nint arg, int low, int high, ref int field, string member)
    {
        if (AdoArgs.Int32(arg) is not int value || value < low || value > high)
        {
            return NotImplemented($"Recordset.{member} = a value outside {low}..{high} (not asked)");
        }
        field = value;
        return CallResult.Nothing;
    }

    /// <summary>Source of a closed Recordset: the text assigned, kept across Close (rs-open-by-properties). Unassigned, not asked.</summary>
    private CallResult GetSource() => _source is null ? NotImplemented("Recordset.Source before one is assigned (not asked)") : Text(_source);

    private CallResult PutSource(nint arg)
    {
        string? text = AdoArgs.IsObject(arg) ? null : Com.Args.Text(arg);
        if (text is null)
        {
            return NotImplemented("Recordset.Source = a Command or another non-text source (not asked)");
        }
        _source = text;
        return CallResult.Nothing;
    }
}

/// <summary>
/// ADODB.Record (_Record) as constructed: closed, with RecordType, ParentURL, Fields and Close refused 0x800A0E78
/// (r-q-ado-misc/record-open-forms). Open is not built: the ACE provider supports none of the Record sources asked
/// (ADO-RECORD), and those answers need an open connection.
/// </summary>
internal sealed class RecordObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Record;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("State", (self, args) => Int32(0))
        .Get("RecordType", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("ParentURL", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Get("Fields", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed))
        .Method("Close", (self, args) => ((RecordObject)self).Fail(AdoErrors.ObjectClosed));

    private RecordObject() : base(Decl, Vtable, "ADODB.Record")
    {
    }

    protected override OperationTable Table => Operations;

    /// <summary>
    /// No aggregation: ACT-AGGREGATE asked Connection, Recordset, Command and Stream, not Record, so an outer is refused
    /// as COM permits (II-13 §4: no aggregation for an unobserved class; open).
    /// </summary>
    public static unsafe int Create(HostBinding binding, nint outer, in Guid iid, nint* result)
    {
        if (outer != 0)
        {
            *result = 0;
            return HResults.CLASS_E_NOAGGREGATION;
        }
        return new RecordObject().Publish(iid, result);
    }
}
