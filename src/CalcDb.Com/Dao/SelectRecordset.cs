using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Transactions;
using CalcDb.Sql;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Recordset (Recordset2) over a SELECT's result (W-06/parser-binder-scan; II-11 §1 ExecutionInstance, II-12 §10): the
/// SQL executor's <see cref="SelectCursor"/>, opened by Database.OpenRecordset with SELECT text or a saved select query,
/// or by QueryDef.OpenRecordset. Its rows, movement (CUR-POS, CUR-POP) and values are the cursor's; a value read that
/// fails is ACE's error from DAO.Field with an IErrorInfo (r-q3-sql/constant-phase). It reads only: the members a
/// result needs beyond Type, EOF, BOF, RecordCount, the moves, Fields and Close are named gaps. After Close every member
/// fails 3420 (R-ACT-1 ACT-RETAIN).
/// </summary>
internal sealed unsafe class SelectRecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("RecordCount", (self, args) => Live(self, r => r.RecordCount()))
        .Get("EOF", (self, args) => Live(self, r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Live(self, r => Bool(r.Cursor.Bof)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Live(self, r => r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Live(self, r => r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Live(self, r => r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Move(rows, start)); })
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        // A query recordset's edits are not built, so no edit is ever pending: a dynaset's Collect put is 3020 from DAO.Field
        // without an IErrorInfo (r-def-cursor-dao/recordset-collect-member); the other types are not recorded.
        .Put("Collect", (self, args) => Live(self, r => r.RecordsetType == 2
            ? CallResult.Fail(Failure.Dao(3020, "DAO.Field", "Update or CancelUpdate without AddNew or Edit."))
            : CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordsetEdit", $"Collect put on a type {r.RecordsetType} query recordset (not recorded)"))));

    /// <summary>
    /// Collect(Item): the value of the column the index names, as Field.Value reads it; an index naming no column (3, -1,
    /// "T.N", Null, Empty) is 3265 from DAO.Fields without an IErrorInfo, names compare case-insensitively
    /// (r-def-cursor-dao/recordset-collect-member).
    /// </summary>
    private CallResult Collect(nint item)
    {
        int? ordinal = CollectionLookup.Find(item, FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CollectObjectIndex", "Collect with an object index (not recorded)"));
        }
        return ordinal is int i ? SelectFieldObject.ReadValue(this, i) : CallResult.Fail(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
    }

    private SelectFieldsObject? _fields;
    private CollectionNames? _fieldNames;
    private readonly ExplicitFrame[] _openLevels;

    /// <param name="plan">The bound statement, owned by this recordset with its cursor.</param>
    public SelectRecordsetObject(DatabaseObject database, BoundSelect plan, SelectCursor cursor, int recordsetType) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        Database = database;
        Plan = plan;
        Cursor = cursor;
        RecordsetType = recordsetType;
        _openLevels = [.. database.Workspace.Transactions.Frames];
        database.AddRef(); // a recordset keeps its Database object alive
    }

    /// <summary>
    /// Set when a Rollback ended a level this recordset was opened inside: no record shows a query recordset there
    /// (TXN-CLOSE records table-type and dynaset recordsets of a table), so every member but Close is named. A Rollback of
    /// a level begun after the open is not recorded either; the recordset keeps answering as before.
    /// </summary>
    public string? NotRecordedAfterRollback { get; private set; }

    public void RolledBack(ExplicitFrame level, bool outermost)
    {
        if (!Closed && Array.IndexOf(_openLevels, level) >= 0)
        {
            NotRecordedAfterRollback ??= $"a query recordset opened inside a level ({level.Level}) that was rolled back, not recorded (TXN-CLOSE)";
        }
    }

    protected override Operation? Find(MemberDecl member) => NotRecordedAfterRollback is { } why && member.Name != "Close"
        ? (self, args) => CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", $"Recordset.{member.Name}: {why}"))
        : base.Find(member);

    /// <summary>
    /// RecordCount: the rows populated so far (CUR-POP), or named where the cursor says the population at open is not
    /// ACE's (<see cref="SelectCursor.RecordCountOpen"/>: a join or union without ORDER BY is read whole at open).
    /// </summary>
    private CallResult RecordCount() => Cursor.RecordCountOpen is { } open
        ? CallResult.Fail(Failure.NotModelled("NotModelled.QueryRecordCount", "RecordCount: " + open))
        : Int32((int)Cursor.RecordCount);

    public DatabaseObject Database { get; }

    public BoundSelect Plan { get; }

    public SelectCursor Cursor { get; }

    /// <summary>RecordsetTypeEnum: 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool Closed { get; private set; }

    public override bool Invalid => Closed;

    /// <summary>The column names for Fields lookups, prepared once (the result's columns do not change).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Columns.Select(c => c.Name)]);

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    private SelectFieldsObject FieldsCollection => _fields ??= new SelectFieldsObject(this);

    private static CallResult Live(ComObject self, Func<SelectRecordsetObject, CallResult> op)
    {
        var r = (SelectRecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    /// <summary>
    /// A move as DAO publishes it: NoCurrentRecord is 3021, a backward move of forward-only 3219 (CUR-POS). A failure met
    /// while the move produced rows (a WHERE failing on a streamed row) is named (EXPR-STAGE: where the cursor then stands
    /// is not identified).
    /// </summary>
    private CallResult Moved(MoveOutcome outcome)
    {
        if (Cursor.FetchFailure is { } fetch)
        {
            return CallResult.Fail(fetch.IsNotModelled ? Failure.NotModelled(fetch.NotModelled!, fetch.Message) : Failure.Dao(fetch.Number, Source, fetch.Message));
        }
        return outcome switch
        {
            MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
            MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
            MoveOutcome.NotSupported when RecordsetType == 8 => Fail(3219, DaoErrors.InvalidOperation),
            _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
        };
    }

    private CallResult Move(nint rowsArg, nint startBookmark)
    {
        if (startBookmark != 0)
        {
            return CallResult.Fail(Failure.NotImplemented("Move with a StartBookmark"));
        }
        int? rows = Args.Int32(rowsArg, omitted: null);
        return rows is null ? CallResult.Fail(Failure.NotImplemented("Move with a Rows argument that is not a number")) : Moved(Cursor.Move(rows.Value));
    }

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
            Plan.Dispose();
            Closed = true;
        }
    }

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

/// <summary>The VARIANT result of a value the SQL layer made: a result cell, or a QueryDef's parameter value.</summary>
internal static class SqlResults
{
    /// <summary>
    /// A BSTR is copied (the value stays its owner's); a Decimal result cell is adopted, as the cursor hands it over;
    /// scalars go inline.
    /// </summary>
    public static CallResult Of(NativeVariant value, string member) => (VarType)value.Vt switch
    {
        VarType.Bstr => CallResult.OfOwnedBstr(Bstrs.FromUnits(Bstrs.Chars(value.Pointer).Length, value.Pointer, static (dest, source) => Bstrs.Chars(source).CopyTo(dest))),
        VarType.Decimal => CallResult.Of(OwnedVariant.Adopt(value)),
        VarType.Empty or VarType.Null or VarType.Bool or VarType.UI1 or VarType.I2 or VarType.I4 or VarType.Currency or VarType.R4 or VarType.R8 or VarType.Date => CallResult.OfScalar((VarType)value.Vt, value.Int64),
        var other => CallResult.Fail(Failure.NotImplemented($"{member} of a {other} value")),
    };
}

/// <summary>
/// Fields of a SELECT recordset: one collection object per recordset, its columns in select-list order. A Field wrapper
/// is shared while a client holds it (R-DAO-1 DAO-ID); lookup follows DAO-COLL.
/// </summary>
internal sealed unsafe class SelectFieldsObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Fields;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Live(self, f => Int16(f.Recordset.Cursor.Columns.Count)))
        .Get("Item", static (self, args) => (SelectFieldsObject)self is { Recordset.Closed: true } closed ? closed.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : ((SelectFieldsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private readonly Dictionary<int, SelectFieldObject> _held = [];

    public SelectFieldsObject(SelectRecordsetObject recordset) : base(Decl, Vtable, recordset.Engine, "DAO.Fields")
    {
        Recordset = recordset;
    }

    public SelectRecordsetObject Recordset { get; }

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<SelectFieldsObject, CallResult> op)
    {
        var f = (SelectFieldsObject)self;
        return f.Recordset.Closed ? f.Recordset.Fail(3420, DaoErrors.ObjectInvalid)
            : f.Recordset.NotRecordedAfterRollback is { } why ? CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", "Fields: " + why)) : op(f);
    }

    private CallResult Item(nint index)
    {
        int? ordinal = CollectionLookup.Find(index, Recordset.FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            return CallResult.Fail(Failure.NotImplemented("Fields.Item with an object index (ACE: 3001 and 3421)"));
        }
        if (ordinal is not int i)
        {
            return Fail(3265, DaoErrors.ItemNotFound);
        }
        if (_held.TryGetValue(i, out SelectFieldObject? held))
        {
            return Object(held);
        }
        var field = new SelectFieldObject(this, i);
        _held[i] = field;
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public void Released(int ordinal) => _held.Remove(ordinal);
}

/// <summary>
/// A result column of a SELECT recordset (Field3): Name, Type, Size, SourceTable and SourceField from the binding
/// (qualified.md "the DAO Field.Type of a projected column"; a type, size or source CalcDb does not identify is named),
/// and Value read from the current row on each get (a held Field follows the current row, R-DAO-1 DAO-ID). A computed
/// column whose static type is not identified gives the evaluator's inner value. Value failures publish an IErrorInfo
/// from DAO.Field (R-ERR-1 ERR-EXCEP; r-q3-sql/constant-phase).
/// </summary>
internal sealed unsafe class SelectFieldObject : DaoObject
{
    private static readonly InterfaceDecl Decl = DaoTypes.Field3;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Name", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("Type", (self, args) => Live(self, f => f.FieldType()))
        .Get("Size", (self, args) => Live(self, f => f.Column.Size is int s ? Int32(s) : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSize", $"Field.Size of the computed column {f.Column.Name}"))))
        .Get("SourceTable", (self, args) => Live(self, f => Origin(f.Column.SourceTable, f.Column.Name)))
        .Get("SourceField", (self, args) => Live(self, f => Origin(f.Column.SourceField, f.Column.Name)))
        .Get("OrdinalPosition", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("Value", (self, args) => ((SelectFieldObject)self).Value());

    private readonly SelectFieldsObject _fields;
    private readonly int _ordinal;

    public SelectFieldObject(SelectFieldsObject fields, int ordinal) : base(Decl, Vtable, fields.Engine, "DAO.Field")
    {
        _fields = fields;
        _ordinal = ordinal;
        fields.AddRef();
    }

    private SelectRecordsetObject Recordset => _fields.Recordset;

    private ResultColumn Column => Recordset.Cursor.Columns[_ordinal];

    protected override OperationTable Table => Operations;

    private static CallResult Live(ComObject self, Func<SelectFieldObject, CallResult> op)
    {
        var f = (SelectFieldObject)self;
        return f.Recordset.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid)
            : f.Recordset.NotRecordedAfterRollback is { } why ? CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", "Field: " + why)) : op(f);
    }

    private CallResult FieldType() => Recordset.Cursor.FieldType(_ordinal, out string? open) is int type
        ? Int16(type)
        : CallResult.Fail(Failure.NotModelled(open ?? "NotModelled.ExpressionFieldType", $"Field.Type of the column {Column.Name}"));

    private static CallResult Origin(string? value, string column) => value is null
        ? CallResult.Fail(Failure.NotModelled("NotModelled.FieldSource", $"Field.SourceTable and SourceField of the column {column} (r-sql-1/b-output-names)"))
        : Text(value);

    private CallResult Value() => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : ReadValue(Recordset, _ordinal);

    /// <summary>The current row's value of column <paramref name="ordinal"/> of an open query recordset (Field.Value, Recordset.Collect).</summary>
    public static CallResult ReadValue(SelectRecordsetObject recordset, int ordinal)
    {
        if (recordset.NotRecordedAfterRollback is { } why)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", "Field.Value: " + why));
        }
        if (!recordset.Cursor.Read(ordinal, out NativeVariant value, out SqlFailure? failure))
        {
            return failure!.IsNotModelled
                ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
                : CallResult.Fail(Failure.Dao(failure.Number, "DAO.Field", failure.Message) with { PublishesErrorInfo = true });
        }
        return SqlResults.Of(value, "Field.Value");
    }

    protected override void OnFinalRelease()
    {
        _fields.Released(_ordinal);
        _fields.Release();
    }
}