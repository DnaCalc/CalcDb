using CalcDb.Catalog;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Expressions;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Scalar;
using CalcDb.Format;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Isam.Transactions;

namespace CalcDb.Com.Dao;

/// <summary>
/// A Recordset (Recordset2) over a native read cursor: the table cursor (W-04/table-cursor) or the populated
/// dynaset/snapshot cursor, with the qualified movement rules (R-CURSOR-1 CUR-POS), counts (CUR-POP) and bookmarks (CUR-ID).
/// After Close every member fails 3420 (R-ACT-1 ACT-RETAIN), with source DAO.Recordset.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "A COM object: its edit frame and cursor end at Close or at the final Release (CloseCursor), not through IDisposable.")]
internal sealed unsafe class RecordsetObject : DaoObject, IDatabaseRecordset
{
    private static readonly InterfaceDecl Decl = DaoTypes.Recordset2;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Type", (self, args) => Live(self, r => Int16(r.RecordsetType)))
        .Get("RecordCount", (self, args) => Read(self, "RecordCount", r => Int32((int)r.Cursor.RecordCount)))
        .Get("EOF", (self, args) => Read(self, "EOF", r => Bool(r.Cursor.Eof)))
        .Get("BOF", (self, args) => Read(self, "BOF", r => Bool(r.Cursor.Bof)))
        .Get("NoMatch", (self, args) => Read(self, "NoMatch", r => Bool(r.Cursor.NoMatch)))
        .Get("Name", (self, args) => Live(self, r => Text(r.TableName)))
        .Get("Fields", (self, args) => Live(self, r => Object(r.FieldsCollection)))
        .Method("MoveFirst", (self, args) => Moving(self, "MoveFirst", r => r.Moved(r.Cursor.MoveFirst())))
        .Method("MoveLast", (self, args) => args.Length > 0 && args[0] != 0 ? CallResult.Fail(Failure.NotImplemented("MoveLast with Options")) : Moving(self, "MoveLast", r => r.Moved(r.Cursor.MoveLast())))
        .Method("MoveNext", (self, args) => Moving(self, "MoveNext", r => r.Moved(r.Cursor.MoveNext())))
        .Method("MovePrevious", (self, args) => Moving(self, "MovePrevious", r => r.Moved(r.Cursor.MovePrevious())))
        .Method("Move", (self, args) => { nint rows = args[0], start = args.Length > 1 ? args[1] : 0; return Moving(self, "Move", r => r.Move(rows, start)); })
        .Get("AbsolutePosition", (self, args) => Read(self, "AbsolutePosition", r => r.AbsolutePosition()))
        .Get("PercentPosition", (self, args) => Read(self, "PercentPosition", r => r.PercentPosition()))
        .Put("PercentPosition", (self, args) => { nint value = args[^1]; return Read(self, "PercentPosition", r => r.SetPercentPosition(value)); })
        .Get("Bookmark", (self, args) => Read(self, "Bookmark", r => r.GetBookmark()))
        .Put("Bookmark", (self, args) => { nint value = args[^1]; return Moving(self, "Bookmark", r => r.SetBookmark(value)); })
        .Get("Index", (self, args) => Live(self, r => r.Cursor is TableCursor t ? Text(t.Index?.Name ?? "") : CallResult.Fail(Failure.NotImplemented("Index on a recordset that is not table-type"))))
        .Put("Index", (self, args) => { nint value = args[^1]; return Read(self, "Index", r => r.EditPending("Index") ?? r.SetIndex(value)); })
        .Method("Requery", (self, args) => Live(self, r => r.Requery()))
        .Method("Seek", static (self, args) => (RecordsetObject)self is { Closed: true } closed ? closed.Fail(3420, DaoErrors.ObjectInvalid) : ((RecordsetObject)self).GuardedSeek(args))
        .Method("Edit", (self, args) => Live(self, r => r.Edit()))
        .Method("AddNew", (self, args) => Live(self, r => r.AddNew()))
        .Method("Update", (self, args) => { nint type = args.Length > 0 ? args[0] : 0, force = args.Length > 1 ? args[1] : 0; return Live(self, r => r.Update(type, force)); })
        .Method("CancelUpdate", (self, args) => { nint type = args.Length > 0 ? args[0] : 0; return Live(self, r => r.CancelUpdate(type)); })
        .Method("Delete", (self, args) => Live(self, r => r.Delete()))
        .Method("FindFirst", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.First, args[0]))
        .Method("FindLast", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Last, args[0]))
        .Method("FindNext", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Next, args[0]))
        .Method("FindPrevious", static (self, args) => ((RecordsetObject)self).FindRow(FindDirection.Previous, args[0]))
        .Method("Close", (self, args) => Live(self, r => r.Close()))
        .Get("Updatable", (self, args) => Live(self, r => Bool(r.Updatable)))
        .Get("Bookmarkable", (self, args) => Live(self, r => Bool(r.Bookmarkable)))
        .Get("LockEdits", (self, args) => Live(self, r => r.RecordsetType is 1 or 2 ? Bool(r._pessimistic) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Transactions", (self, args) => Live(self, r => Bool(r.RecordsetType == 1)))
        .Get("Restartable", (self, args) => Live(self, r => r.RecordsetType == 2 ? Bool(true) : r.RecordsetType == 1 ? Bool(false) : CallResult.Fail(Failure.NotImplemented("Recordset.Restartable of a snapshot or forward-only recordset (not observed)"))))
        .Get("EditMode", (self, args) => Live(self, r => Int16((short)(r._frame?.Mode ?? EditMode.None))))
        .Get("Filter", (self, args) => Live(self, r => r.RecordsetType == 1 ? r.Fail(3251, DaoErrors.NotSupportedForType) : r.RecordsetType == 2 ? Text("") : CallResult.Fail(Failure.NotImplemented("Recordset.Filter of a snapshot or forward-only recordset (not observed)"))))
        .Get("Sort", (self, args) => Live(self, r => r.RecordsetType == 1 ? r.Fail(3251, DaoErrors.NotSupportedForType) : r.RecordsetType == 2 ? Text("") : CallResult.Fail(Failure.NotImplemented("Recordset.Sort of a snapshot or forward-only recordset (not observed)"))))
        .Get("CacheSize", (self, args) => Live(self, r => r.RecordsetType == 1 ? r.Fail(3251, DaoErrors.NotSupportedForType) : r.RecordsetType == 2 ? Int32(0) : CallResult.Fail(Failure.NotImplemented("Recordset.CacheSize of a snapshot or forward-only recordset (not observed)"))))
        .Get("DateCreated", (self, args) => Live(self, r => r.RecordsetType == 1 ? CatalogDates.Date(r._entry.DateCreateBits) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("LastUpdated", (self, args) => Live(self, r => r.RecordsetType == 1 ? CatalogDates.Date(r._entry.DateUpdateBits) : r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("ValidationRule", (self, args) => Live(self, r => r.TableStored("ValidationRule")))
        .Get("ValidationText", (self, args) => Live(self, r => r.TableStored("ValidationText")))
        .Get("StillExecuting", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Connection", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchCollisionCount", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchCollisions", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("BatchSize", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("RecordStatus", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("UpdateOptions", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("Properties", (self, args) => Live(self, r => r.PropertiesCollection()));

    private FieldsObject? _fields;
    private PropertiesObject? _properties;
    private readonly CatalogEntry _entry;
    private readonly int _options;
    private readonly bool _pessimistic;
    private readonly int _openDepth;
    private EditFrame? _frame;

    /// <param name="options">The admitted OpenRecordset Options bits (<see cref="RecordsetOptions"/>).</param>
    /// <param name="pessimistic">LockEdits: True unless LockEdit dbOptimistic (3) was given (r-fix-cursor-dao/updatable-members-rw).</param>
    public RecordsetObject(DatabaseObject database, CatalogEntry entry, IRecordCursor cursor, int recordsetType, int options, bool pessimistic) : base(Decl, Vtable, database.Engine, "DAO.Recordset")
    {
        Database = database;
        _entry = entry;
        TableName = entry.Name;
        Cursor = cursor;
        RecordsetType = recordsetType;
        _options = options;
        _pessimistic = pessimistic;
        _openDepth = database.Workspace.Transactions.Depth;
        database.AddRef(); // a recordset keeps its Database object alive
    }

    /// <summary>Updatable (R-CURSOR-1 CUR-PROV with the option matrix of r-fix-cursor-dao/option-flags-rw): <see cref="RecordsetOptions.Updatable"/>.</summary>
    private bool Updatable => RecordsetOptions.Updatable(RecordsetType, _options, Database.ReadOnly);

    private bool Bookmarkable => RecordsetOptions.Bookmarkable(RecordsetType, _options);

    public override bool Invalid => Closed;

    /// <summary>ValidationRule and ValidationText: the table's stored value (CAT-P), "" when none is stored (r-dao-1b/recordset-properties-*).</summary>
    private CallResult TableStored(string name)
    {
        PropertyMap? stored = Database.StoredProperties(_entry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset member when the table's property blob cannot be read: " + failure));
        }
        StoredProperty? p = stored?.Properties.FirstOrDefault(p => p.Name == name);
        return p is null ? Text("") : PropertyObject.StoredValue(p);
    }

    /// <summary>The 32 built-ins, then the table's own stored properties (r-dao-1b/recordset-properties-table and -dynaset: Description).</summary>
    private CallResult PropertiesCollection()
    {
        PropertyMap? stored = Database.StoredProperties(_entry, out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Recordset.Properties when the table's property blob cannot be read: " + failure));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.Recordset, stored));
        return Object(_properties);
    }

    public DatabaseObject Database { get; }

    public string TableName { get; }

    public IRecordCursor Cursor { get; }

    /// <summary>The field names for Fields lookups, prepared once per recordset (its columns do not change; task fbh8).</summary>
    public CollectionNames FieldNames => _fieldNames ??= new CollectionNames([.. Cursor.Table.Columns.Select(c => c.Name)]);

    private CollectionNames? _fieldNames;

    /// <summary>RecordsetTypeEnum: 1 table, 2 dynaset, 4 snapshot, 8 forward-only.</summary>
    public int RecordsetType { get; }

    public bool Closed { get; private set; }

    protected override OperationTable Table => Operations;

    protected override Failure? ArgumentCountFailure(MemberDecl member) => InvalidArgumentCount();

    /// <summary>
    /// A scalar argument that does not coerce is 3421 (CUR-POS: Move(Null) and Move("abc"), r-cursor-1b/move-argument-forms;
    /// PercentPosition = Null, percent-assign); Missing for a required one is 3001 (Move(Missing); Seek(Missing, 4),
    /// seek-operator-spelling). Both publish IErrorInfo.
    /// </summary>
    protected override Failure? ArgumentConversionFailure(MemberDecl member, bool missing) =>
        missing ? InvalidArgumentCount() : Failure.Dao(3421, Source, DaoErrors.ConversionError) with { PublishesErrorInfo = true };

    /// <summary>The Find criteria coerces a VT_BOOL as "True" (VARIANT_ALPHABOOL; R-CURSOR-1 CUR-FIND, find-expression-forms against findfirst-fresh-recordset).</summary>
    protected override ushort CoercionFlags(MemberDecl member, int parameter) =>
        parameter == 0 && member.Name is "FindFirst" or "FindLast" or "FindNext" or "FindPrevious" ? VariantAlphaBool : (ushort)0;

    private const ushort VariantAlphaBool = 0x2;

    private FieldsObject FieldsCollection => _fields ??= new FieldsObject(this);

    /// <summary>Index put on a table-type recordset: the first row of the new order becomes current; an unknown name is 3800 (r-cursor-1/id-index-switch).</summary>
    private CallResult SetIndex(nint value)
    {
        if (Cursor is not TableCursor table)
        {
            return CallResult.Fail(Failure.NotImplemented("Index on a recordset that is not table-type"));
        }
        string name = Args.Text(value) ?? "";
        return table.SetIndex(name) switch
        {
            IndexRefusal.None => CallResult.Nothing,
            IndexRefusal.UnknownIndex => Fail(3800, DaoErrors.NotAnIndex(name)),
            var other => CallResult.Fail(Failure.NotImplemented("Index put: " + other + " " + table.LastTraversalFailure?.Predicate)),
        };
    }

    /// <summary>
    /// Seek(Comparison, Key1, … Key13) (R-CURSOR-1 CUR-SEEK, qualified): a dynaset refuses with 3251, an unknown comparison
    /// with 3001, no Index with 3019, a key that does not convert with 3421, a Null or Empty key for a Yes/No column with
    /// 3162, then more keys than index columns with 3250 (r-cursor-1-seek-keys/yes-no-null-key: (a, Null, 1) on a
    /// two-column index is 3162); a refusal leaves the position and NoMatch. A Missing key cuts the key there; otherwise
    /// Null and Empty are the Null key value.
    /// </summary>
    private CallResult Seek(ReadOnlySpan<nint> args)
    {
        if (Cursor is not TableCursor table)
        {
            return Fail(3251, DaoErrors.NotSupportedForType);
        }
        // The comparison as the BSTR's own characters (no string per call, task v4n6); other types convert as before.
        // An omitted comparison (pointer 0) names no operator, as Args.Text gives it.
        SeekOperator? op = (args[0] != 0 && Args.TypeOf(args[0]) == VarType.Bstr ? Bstrs.Chars(Args.Cell(args[0])->Pointer) : (Args.Text(args[0]) ?? "").AsSpan()) switch
        {
            "=" => SeekOperator.Equal,
            ">=" => SeekOperator.GreaterOrEqual,
            ">" => SeekOperator.Greater,
            "<=" => SeekOperator.LessOrEqual,
            "<" => SeekOperator.Less,
            _ => null,
        };
        if (op is null)
        {
            return Fail(3001, DaoErrors.InvalidArgument);
        }
        if (table.Index is not { } logical || table.Table.PhysicalOf(logical) is not { } physical)
        {
            return Fail(3019, DaoErrors.NoCurrentIndex);
        }
        int given = 0;
        while (1 + given < args.Length && args[1 + given] != 0)
        {
            given++;
        }
        // The keys that have a column convert first, the count is checked after them (yes-no-null-key). At most 10 keys
        // convert (no index has more columns), into arrays this recordset keeps; Text keys borrow its per-key character
        // buffers, so a steady Seek allocates nothing (tasks v4n6, 73vf).
        int columns = physical.Columns.Count;
        Span<SeekKey> keys = (_seekKeys ??= new SeekKey[MaxSeekKeys]).AsSpan(0, Math.Min(given, columns));
        for (int i = 0; i < keys.Length; i++)
        {
            ColumnDescriptor? column = table.KeyColumn(i);
            KeyArgumentFailure failure = KeyArgumentFailure.NotEncoded;
            SeekKey? key = column is null ? null
                : IndexKeys.IsText(column.TypeCode) ? SeekTextKeyOf(i, args[1 + i], out failure)
                : SeekKeyOf(Args.Cell(args[1 + i]), column.TypeCode, out failure);
            if (key is null)
            {
                return failure switch
                {
                    KeyArgumentFailure.Conversion => Fail(3421, DaoErrors.ConversionError),
                    KeyArgumentFailure.NullForYesNo => Fail(3162, DaoErrors.NullToNonVariant),
                    _ => CallResult.Fail(Failure.NotImplemented($"Seek on an index column of native type {column?.TypeCode} (W-04/private-codecs)")),
                };
            }
            keys[i] = key.Value;
        }
        if (given > columns)
        {
            return Fail(3250, DaoErrors.CouldNotBuildKey);
        }
        IndexRefusal refusal = table.Seek(op.Value, keys, out _);
        return refusal switch
        {
            IndexRefusal.None => CallResult.Nothing,
            IndexRefusal.TooManyKeys => Fail(3250, DaoErrors.CouldNotBuildKey),
            IndexRefusal.KeyNotEncodable => CallResult.Fail(Failure.NotImplemented("Seek on an index whose key this codec cannot encode (GUID, Binary and NUMERIC keys are W-04/private-codecs)")),
            IndexRefusal.SortOrderNotModelled => CallResult.Fail(Failure.NotImplemented("NotModelled.SortOrder: an indexed Text seek on a sort order outside General, General - Legacy and General 97 (TextSeek)")),
            _ => CallResult.Fail(Failure.NotImplemented("Seek: " + refusal + " " + table.LastTraversalFailure?.Predicate)),
        };
    }

    private const int MaxSeekKeys = 13;

    private SeekKey[]? _seekKeys;
    private char[]?[]? _seekText;

    /// <summary>Why a Seek argument gives no key value.</summary>
    private enum KeyArgumentFailure
    {
        None,

        /// <summary>A column type the codec does not encode (W-04/private-codecs).</summary>
        NotEncoded,

        /// <summary>3421: the argument does not convert to the column's type.</summary>
        Conversion,

        /// <summary>
        /// 3162: Null or Empty for a Yes/No column, under every operator and position (r-sort-3b/mix-seek-b; r-cursor-1-
        /// seek-keys/yes-no-null-key). A Yes/No column holds no Null, and Empty does not convert to False here.
        /// </summary>
        NullForYesNo,
    }

    /// <summary>
    /// A Seek key for a Text or Memo column: Null and Empty are the Null key; a BSTR is its own units, all of them (CUR-SEEK:
    /// ACE compares a Seek key whole, <see cref="TextKeyExtent.WholeSeekArgument"/>), copied into this recordset's buffer for
    /// that key, which grows once to the longest key seen; another type converts to a BSTR as VariantChangeType does, or
    /// fails with 3421.
    /// </summary>
    private SeekKey? SeekTextKeyOf(int ordinal, nint arg, out KeyArgumentFailure failure)
    {
        failure = KeyArgumentFailure.None;
        NativeVariant* v = Args.Cell(arg);
        VarType vt = (VarType)v->Vt & ~VarType.ByRef;
        if (vt is VarType.Null or VarType.Empty)
        {
            return new SeekKey(KeyValue.Null);
        }
        ReadOnlySpan<char> units;
        string? convertedText = null;
        if (Args.TypeOf(arg) == VarType.Bstr)
        {
            units = v->Pointer == 0 ? default : Bstrs.Chars(v->Pointer);
        }
        else
        {
            convertedText = Args.Text(arg);
            if (convertedText is null)
            {
                failure = KeyArgumentFailure.Conversion;
                return null;
            }
            units = convertedText;
        }
        char[]?[] buffers = _seekText ??= new char[MaxSeekKeys][];
        if (buffers[ordinal] is not { } buffer || buffer.Length < units.Length)
        {
            buffer = buffers[ordinal] = new char[Math.Max(units.Length, TextIndexUnits)];
        }
        units.CopyTo(buffer);
        return new SeekKey(KeyValue.OfText(buffer.AsMemory(0, units.Length)));
    }

    /// <summary>The units a stored Text entry keeps, the first size of a key buffer.</summary>
    private const int TextIndexUnits = 255;

    /// <summary>
    /// A Seek key as its column's stored value: Null and Empty are the Null key, except for a Yes/No column (3162);
    /// integer columns take the numeric value (a fraction lies between two stored values); Currency, Single, Double, Date
    /// and Boolean convert as VariantChangeType does, or fail with 3421. Null with <paramref name="failure"/>
    /// NotEncoded is a column type the codec does not encode.
    /// </summary>
    private static SeekKey? SeekKeyOf(NativeVariant* v, byte type, out KeyArgumentFailure failure)
    {
        failure = KeyArgumentFailure.None;
        VarType vt = (VarType)v->Vt & ~VarType.ByRef;
        if (vt is VarType.Null or VarType.Empty)
        {
            if (type == NativeTypes.Boolean)
            {
                failure = KeyArgumentFailure.NullForYesNo;
                return null;
            }
            return new SeekKey(KeyValue.Null);
        }
        VarType? target = type switch
        {
            NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long or NativeTypes.Double => VarType.R8,
            NativeTypes.Single => VarType.R4,
            NativeTypes.Currency => VarType.Currency,
            NativeTypes.DateTime => VarType.Date,
            NativeTypes.Boolean => VarType.Bool,
            _ => null,
        };
        if (target is null)
        {
            failure = KeyArgumentFailure.NotEncoded;
            return null;
        }
        if (!OwnedVariant.TryChangeScalar(v, target.Value, out NativeVariant changed, out _))
        {
            failure = KeyArgumentFailure.Conversion;
            return null;
        }
        NativeVariant* x = &changed;
        switch (type)
        {
            case NativeTypes.Byte or NativeTypes.Integer or NativeTypes.Long:
            {
                double d = x->Double;
                double floor = Math.Floor(d);
                (long min, long max) = type switch { NativeTypes.Byte => (0L, 255L), NativeTypes.Integer => (short.MinValue, short.MaxValue), _ => (int.MinValue, int.MaxValue) };
                if (double.IsNaN(d) || floor < min || floor > max)
                {
                    failure = KeyArgumentFailure.Conversion;
                    return null;
                }
                return new SeekKey(new KeyValue(KeyValueKind.Integer, Integer: (long)floor), InexactAbove: floor != d);
            }
            case NativeTypes.Double or NativeTypes.DateTime:
                return new SeekKey(new KeyValue(KeyValueKind.FloatBits, Bits: (ulong)x->Int64));
            case NativeTypes.Single:
                return new SeekKey(new KeyValue(KeyValueKind.FloatBits, Bits: (uint)*(int*)&x->Int64));
            case NativeTypes.Currency:
                return new SeekKey(new KeyValue(KeyValueKind.ScaledCurrency, Integer: x->Int64));
            default:
                return new SeekKey(new KeyValue(KeyValueKind.Boolean, Boolean: (short)x->Int64 != 0));
        }
    }

    /// <summary>
    /// FindFirst, FindLast, FindNext, FindPrevious (R-CURSOR-1 CUR-FIND; qualified.md "R-EXPR-1 EXPR-BIND, EXPR-INVENTORY
    /// (Find criteria)"). The criteria arrives as text (the binder coerced it, a VT_BOOL as "True"; Null is 3421 and
    /// Missing 3001 there, with IErrorInfo). It is bound once per text and kept (W-06/restricted-scalar, task x866: a
    /// repeated FindNext binds nothing). Table-type refuses with 3251 and forward-only with 3219; a criteria that does
    /// not bind on those is the not-recorded order of the two refusals. The cursor applies the position rules; a
    /// criteria failure is ACE's error with EXCEPINFO alone, entered in Errors.
    /// </summary>
    private CallResult FindRow(FindDirection direction, nint argument)
    {
        if (Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid);
        }
        ReadOnlySpan<char> text = argument != 0 && Args.TypeOf(argument) == VarType.Bstr ? Bstrs.Chars(Args.Cell(argument)->Pointer) : (Args.Text(argument) ?? "").AsSpan();
        FindCriteria? criteria = Criteria(text, out ExpressionFailure? failure);
        if (Cursor is not PopulationCursor { ForwardOnly: false } cursor)
        {
            if (EditPending("Find") is { } pending)
            {
                return pending;
            }
            if (criteria is null)
            {
                return CallResult.Fail(Failure.NotModelled("NotModelled.FindRefusalOrder", "a criteria that does not bind on a table-type or forward-only recordset: which error ACE reports first is not recorded (CUR-FIND)"));
            }
            return Cursor is TableCursor ? Fail(3251, DaoErrors.NotSupportedForType) : Fail(3219, DaoErrors.InvalidOperation);
        }
        if (failure is { IsNotModelled: true })
        {
            return Published(failure);
        }
        if (UnpublishedRead("Find") is { } unpublished)
        {
            return unpublished;
        }
        _frame?.Discard(); // FindFirst and its siblings discard a pending edit (CUR-EDIT)
        FindResult result = criteria is null ? cursor.CriteriaFailed(direction) : cursor.Find(direction, criteria);
        return result.Outcome switch
        {
            FindOutcome.Found or FindOutcome.NotFound => CallResult.Nothing,
            FindOutcome.Failed => Published(criteria is null ? failure! : criteria.LastFailure!),
            _ => CallResult.Fail(Failure.NotModelled("NotModelled.Find." + result.Open, "a Find position the records do not decide (qualified.md CUR-FIND, Open)")),
        };
    }

    /// <summary>An expression failure as DAO publishes it: ACE's number and text from DAO.Recordset, or the named case.</summary>
    private CallResult Published(ExpressionFailure failure) => failure.IsNotModelled
        ? CallResult.Fail(Failure.NotModelled(failure.NotModelled!, failure.Message))
        : Fail(failure.Number, failure.Message);

    private FindCriteria? _criteria;
    private ExpressionSchema? _schema;
    private ExpressionContext? _context;

    /// <summary>The bound criteria for this text: the kept one when the text is the same, else a new binding (kept when it binds).</summary>
    private FindCriteria? Criteria(ReadOnlySpan<char> text, out ExpressionFailure? failure)
    {
        failure = null;
        if (_criteria is { } kept && text.SequenceEqual(kept.Text))
        {
            return kept;
        }
        _schema ??= new ExpressionSchema(Cursor.Table);
        _context ??= new ExpressionContext((uint)System.Globalization.CultureInfo.CurrentCulture.LCID, TextCompareMode.Database, Database.Collation, ExpressionUse.Find);
        FindCriteria? bound = FindCriteria.Bind(text.ToString(), _schema, _context, out failure);
        if (bound is not null)
        {
            _criteria?.Dispose();
            _criteria = bound;
        }
        return bound;
    }

    private static CallResult Live(ComObject self, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : op(r);
    }

    /// <summary>A member that reads the table's rows or membership: refused by name while accepted changes of the table are unpublished.</summary>
    private static CallResult Read(ComObject self, string member, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        return r.Closed ? r.Fail(3420, DaoErrors.ObjectInvalid) : r.UnpublishedRead(member) ?? op(r);
    }

    /// <summary>A move or a Bookmark assignment: it discards a pending edit (CUR-EDIT, edit-move-close-buffered), then reads as <see cref="Read"/>.</summary>
    private static CallResult Moving(ComObject self, string member, Func<RecordsetObject, CallResult> op)
    {
        var r = (RecordsetObject)self;
        if (r.Closed)
        {
            return r.Fail(3420, DaoErrors.ObjectInvalid);
        }
        r._frame?.Discard();
        return r.UnpublishedRead(member) ?? op(r);
    }

    /// <summary>The Workspace whose levels and changes this recordset's edits join.</summary>
    private TransactionCoordinator Transactions => Database.Workspace.Transactions;

    /// <summary>
    /// The refusal of a read that would meet an accepted change this recordset's Workspace sees and the file does not hold
    /// yet (<see cref="UnpublishedChanges"/>), or null. <paramref name="own"/> leaves out the changes of this recordset's
    /// own edit semantics, which the edit frame checks itself (an Edit of a changed row, a unique key).
    /// </summary>
    public CallResult? UnpublishedRead(string member, MutationSemantics? own = null) =>
        Database.Engine.Changes.Visible(Transactions, Database.Name, _entry.Name, own) ? EditPublication.Unpublished(_entry.Name, member) : null;

    /// <summary>
    /// A member whose effect on a pending edit no record shows (Seek, an Index put, a refused Requery or Find): named while
    /// an edit is pending; null otherwise.
    /// </summary>
    private CallResult? EditPending(string member) => _frame is { Mode: not EditMode.None }
        ? CallResult.Fail(Failure.NotModelled("NotModelled.Edit.PendingAcross" + member, $"{member} with an edit pending: whether it keeps or discards the edit is not recorded (CUR-EDIT)"))
        : null;

    private CallResult GuardedSeek(ReadOnlySpan<nint> args) => EditPending("Seek") ?? UnpublishedRead("Seek") ?? Seek(args);

    /// <summary>
    /// This recordset's edit frame (II-07 §13; W-05/edit-frames), made at its first edit member over the table's shared
    /// <see cref="TableEditContext"/>: updatable for table-type and dynaset, not for snapshot and forward-only (CUR-EDIT:
    /// Edit, AddNew, Update and CancelUpdate 3251, a put 3027; editmode-by-type, assign-without-edit-by-type). A table-type
    /// or dynaset recordset that is not updatable (dbReadOnly, a read-only Database) has no record of its edit errors.
    /// </summary>
    public EditFrame? Frame(out CallResult refused)
    {
        refused = default;
        if (_frame is { } frame)
        {
            return frame;
        }
        if (RecordsetType is 1 or 2 && !Updatable)
        {
            refused = CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ReadOnlyRecordset", "an edit member of a table-type or dynaset recordset that is not updatable (dbReadOnly, dbForwardOnly or a read-only Database): ACE's errors are not recorded"));
            return null;
        }
        if (Database.EditContext(_entry, out string? failure) is not { } context)
        {
            refused = CallResult.Fail(Failure.NotImplemented("an edit member when the table's edit semantics cannot be compiled: " + failure));
            return null;
        }
        _frame = new EditFrame(context, Cursor, Transactions, updatable: RecordsetType is 1 or 2);
        return _frame;
    }

    /// <summary>Edit: the frame's Existing buffer over the current row (CUR-EDIT); 3251 on a snapshot, 3021 without a current row.</summary>
    private CallResult Edit()
    {
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        return (frame.Updatable ? UnpublishedRead("Edit", frame.Table.Semantics) : null) ?? EditPublication.Of(frame.Edit());
    }

    /// <summary>
    /// AddNew: a New buffer with the issued AutoNumber and the sampled defaults (CUR-EDIT, EXPR-STAGE). When another
    /// counter of this file's table has issued values the file does not hold yet, the value this one would issue is named.
    /// </summary>
    private CallResult AddNew()
    {
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && frame.Table.Counter is { } counter && Database.Engine.Changes.CounterAhead(Database.Name, _entry.Name, counter))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.UnpublishedCounter", $"another Database of this engine issued AutoNumber values of '{_entry.Name}' that the file's counter does not hold yet (new rows are W-05/growth-structural-closure)"));
        }
        return EditPublication.Of(frame.AddNew());
    }

    /// <summary>
    /// Update(UpdateType, Force): the binder coerced the declared Long and Boolean (update-arguments: Update("1") succeeds,
    /// Update(Null) is 3421 from DAO.Recordset with an IErrorInfo); 1 and False are the frame's only accepted values
    /// (3001 otherwise). The accepted change joins the Workspace's level, or awaits publication outside any level.
    /// </summary>
    private CallResult Update(nint typeArg, nint forceArg)
    {
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        int type = Args.Int32(typeArg, omitted: 1) ?? 1;
        bool force = Args.Bool(forceArg, omitted: false) ?? false;
        return (frame.Updatable && frame.Mode != EditMode.None ? UnpublishedRead("Update", frame.Table.Semantics) : null) ?? EditPublication.Of(frame.Update(type, force));
    }

    /// <summary>CancelUpdate(UpdateType): dbUpdateRegular drops the pending edit without Rollback (CUR-EDIT); another type is not recorded.</summary>
    private CallResult CancelUpdate(nint typeArg)
    {
        if (Args.Int32(typeArg, omitted: 1) is not 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.CancelUpdateType", "CancelUpdate with an UpdateType other than dbUpdateRegular (batch updates are not recorded)"));
        }
        return Frame(out CallResult refused) is { } frame ? EditPublication.Of(frame.CancelUpdate()) : refused;
    }

    /// <summary>Delete: a pending Edit or AddNew is discarded (editmode-unusual-transitions: EditMode 0); deleting a row is W-05/growth-structural-closure.</summary>
    private CallResult Delete()
    {
        _frame?.Discard();
        return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.Delete", "Recordset.Delete: removing a row is W-05/growth-structural-closure"));
    }

    /// <summary>The movement outcome as DAO publishes it: NoCurrentRecord is 3021 (CUR-POS).</summary>
    private CallResult Moved(MoveOutcome outcome) => outcome switch
    {
        MoveOutcome.Moved or MoveOutcome.ReachedBoundary => CallResult.Nothing,
        MoveOutcome.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
        // Forward-only refuses backward movement with 3219 (CUR-POS, r-cursor-1/pos-moves-forwardonly).
        MoveOutcome.NotSupported when RecordsetType == 8 => Fail(3219, DaoErrors.InvalidOperation),
        _ => CallResult.Fail(Failure.NotImplemented($"Recordset movement outcome {outcome}")),
    };

    /// <summary>
    /// AbsolutePosition: the zero-based ordinal on a dynaset or snapshot; forward-only refuses it with 3219 and table-type
    /// with 3251 (r-cursor-1/pos-moves-dynaset, pos-moves-forwardonly, pop-count-table). Without a current row: not observed.
    /// </summary>
    private CallResult AbsolutePosition() => Cursor switch
    {
        TableCursor => Fail(3251, DaoErrors.NotSupportedForType),
        PopulationCursor { ForwardOnly: true } => Fail(3219, DaoErrors.InvalidOperation),
        PopulationCursor { Ordinal: int ordinal } => Int32(ordinal),
        _ => CallResult.Fail(Failure.NotImplemented("AbsolutePosition without a current row")),
    };

    /// <summary>
    /// PercentPosition (R-CURSOR-1 CUR-POP): forward-only refuses it with 3219 as it does AbsolutePosition
    /// (r-cursor-1/pop-count-forwardonly); without a current row a dynaset or snapshot gives 3021; the scales are the
    /// cursors' (TableCursor, PopulationCursor). The value is a Single.
    /// </summary>
    private CallResult PercentPosition()
    {
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        CursorFailure? failure = null;
        float? value = Cursor switch
        {
            TableCursor t => t.PercentPosition(out failure),
            PopulationCursor p => p.PercentPosition(out failure),
            _ => null,
        };
        return value is float v ? CallResult.OfScalar(VarType.R4, BitConverter.SingleToUInt32Bits(v)) : PositionFailure(failure, "PercentPosition");
    }

    /// <summary>
    /// PercentPosition assignment: the value converts to Single as VariantChangeType does (a digit string converts, Empty
    /// is 0, Null fails 3421; r-cursor-1b/percent-assign); out of range is 3001 and leaves the position; forward-only 3219.
    /// </summary>
    private CallResult SetPercentPosition(nint value)
    {
        if (Cursor is PopulationCursor { ForwardOnly: true })
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!OwnedVariant.TryChangeScalar(Args.Cell(value), VarType.R4, out NativeVariant changed, out _))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        float percent = BitConverter.Int32BitsToSingle((int)changed.Int64);
        CursorFailure? failure = Cursor switch
        {
            TableCursor t => t.SetPercentPosition(percent),
            PopulationCursor p => p.SetPercentPosition(percent),
            _ => CursorFailure.NotObserved,
        };
        return failure is null ? CallResult.Nothing : PositionFailure(failure, "PercentPosition put");
    }

    private CallResult PositionFailure(CursorFailure? failure, string member) => failure switch
    {
        CursorFailure.NoCurrentRecord => Fail(3021, DaoErrors.NoCurrentRecord),
        CursorFailure.OutOfRange => Fail(3001, DaoErrors.InvalidArgument),
        _ => CallResult.Fail(Failure.NotImplemented($"{member}: {failure}")),
    };

    /// <summary>Requery: table-type refuses it with 3251 (r-cursor-1/pop-count-table); a dynaset or snapshot's re-population is not built yet.</summary>
    private CallResult Requery()
    {
        if (Cursor is TableCursor)
        {
            return EditPending("Requery") ?? Fail(3251, DaoErrors.NotSupportedForType);
        }
        _frame?.Discard(); // Requery discards a pending edit (CUR-EDIT)
        return CallResult.Fail(Failure.NotImplemented("Requery of a dynaset or snapshot (R-CURSOR-1 CUR-LIFE)"));
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

    private CallResult GetBookmark()
    {
        if (!Bookmarkable && RecordsetType != 8)
        {
            return CallResult.Fail(Failure.NotImplemented("Bookmark of a recordset opened with dbForwardOnly (its cursor is not modelled)"));
        }
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

    /// <summary>
    /// Close: a pending edit is discarded (CUR-EDIT), the cursor's pins and bound row are released and the Database forgets
    /// this recordset. A dynaset opened outside any level and closed inside the first one is 3246 "Operation not supported
    /// in transactions." from DAO.Recordset, the recordset and the level kept; a table-type recordset closes (qualified.md
    /// R-TXN-1 TXN-CLOSE: q2/txn-close-recordset-opened-before-level, 6 of 7 variants). Other recordset types and depths
    /// are not recorded and are named.
    /// </summary>
    private CallResult Close()
    {
        int depth = Database.Workspace.Transactions.Depth;
        if (RecordsetType != 1 && depth > _openDepth)
        {
            return RecordsetType == 2 && _openDepth == 0 && depth == 1
                ? Fail(3246, "Operation not supported in transactions.")
                : CallResult.Fail(Failure.NotModelled("NotModelled.Txn.CloseRecordsetOpenedOutsideLevel", $"Close of a type {RecordsetType} recordset opened at depth {_openDepth} and closed at depth {depth} (TXN-CLOSE records a dynaset from depth 0 to 1)"));
        }
        CloseCursor();
        Database.Forget(this);
        return CallResult.Nothing;
    }

    public void CloseCursor()
    {
        if (!Closed)
        {
            _frame?.Dispose();
            _frame = null;
            Cursor.Dispose();
            _criteria?.Dispose();
            _criteria = null;
            Closed = true;
        }
    }

    public void FieldsReleased() => _fields = null;

    /// <summary>The edit frame when an edit member has made it, else null (EditMode 0, ValidateOnSet False).</summary>
    public EditFrame? FrameIfMade => _frame;

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
        .Get("Item", static (self, args) => (FieldsObject)self is { Recordset.Closed: true } closed ? closed.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : ((FieldsObject)self).Item(args[0]))
        .Method("Refresh", (self, args) => Live(self, f => CallResult.Nothing));

    private readonly Dictionary<int, FieldObject> _held = [];

    // Field wrappers released by their client, one per column at most, handed out again by Item (task hdw6).
    private readonly Dictionary<int, FieldObject> _parked = [];

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
        int? ordinal = CollectionLookup.Find(index, Recordset.FieldNames, out bool objectIndex);
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
        FieldObject field = _parked.Remove(i, out FieldObject? parked) ? parked.Reuse() : new FieldObject(this, i);
        _held[i] = field;
        CallResult result = Object(field);
        field.Release();
        return result;
    }

    public void Released(int ordinal) => _held.Remove(ordinal);

    /// <summary>Keep a released Field wrapper for its column; false when one is kept already or the recordset is closed.</summary>
    public bool Park(int ordinal, FieldObject field) => !Recordset.Closed && _parked.TryAdd(ordinal, field);

    protected override void OnFinalRelease()
    {
        foreach (FieldObject field in _parked.Values)
        {
            field.Discard();
        }
        _parked.Clear();
        base.OnFinalRelease();
    }
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
        .Get("Value", (self, args) => ((FieldObject)self).Value())
        .Put("Value", (self, args) => ((FieldObject)self).Assign(args[^1]))
        .Get("ValidateOnSet", (self, args) => Live(self, f => Bool(f.Recordset.FrameIfMade?.ValidateOnSet(f.Column) ?? false)))
        .Put("ValidateOnSet", (self, args) => { nint value = args[^1]; return Live(self, f => f.SetValidateOnSet(value)); });

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
    private int? DaoType() => ColumnFacts.DaoType(Column);

    private int? Size() => ColumnFacts.Size(Column);

    private CallResult Value()
    {
        if (Recordset.Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true);
        }
        ColumnDescriptor column = Column;
        if (Recordset.FrameIfMade is { Mode: not EditMode.None } frame)
        {
            // During an edit the field reads its buffer cell: the accepted value, the stored original, or Null when unset (CUR-EDIT).
            return Published(frame.Read(column), column);
        }
        if (Recordset.UnpublishedRead("Field.Value") is { } unpublished)
        {
            return unpublished;
        }
        if (column.TypeCode is NativeTypes.Text or NativeTypes.Memo)
        {
            // D-STRING-1 (task n7mw): the final BSTR is built straight from the encoded field bytes.
            TextFieldRead text = Recordset.Cursor.ReadText(column);
            if (text.HasText)
            {
                return CallResult.OfOwnedBstr(Bstrs.FromUnits(text.Shape.Units, text, static (dest, t) => t.DecodeInto(dest)));
            }
            return Published(text.Result, column);
        }
        return Published(Recordset.Cursor.Read(column), column);
    }

    /// <summary>
    /// A Value put (II-10 §21 F-91; W-05/edit-frames): the value, read through one by-reference level, converts into the
    /// frame's temporary candidate and is accepted, or fails from DAO.Field with an IErrorInfo (3020 without an edit, 3027
    /// on a snapshot, 3421 a conversion, 3316 under ValidateOnSet; assign-without-edit-by-type, stage-validate-on-set).
    /// </summary>
    private CallResult Assign(nint value)
    {
        if (Recordset.Closed)
        {
            return Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true);
        }
        if (Recordset.Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        return EditPublication.Of(frame.Assign(Column, incoming));
    }

    /// <summary>ValidateOnSet (EXPR-STAGE): True runs the field's rule at the put, False at Update.</summary>
    private CallResult SetValidateOnSet(nint value)
    {
        if (Recordset.Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        frame.SetValidateOnSet(Column, Args.Bool(value, omitted: false) ?? false);
        return CallResult.Nothing;
    }

    /// <summary>The published result of a field read that is not borrowed text.</summary>
    private CallResult Published(FieldResult read, ColumnDescriptor column)
    {
        if (read.Failure == CursorFailure.NoCurrentRecord)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord, publishesErrorInfo: true);
        }
        if (!read.Succeeded)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Value when the row cannot be read: {read.Failure} {read.Detail}"));
        }
        if (Scalar(read.Value) is { } scalar)
        {
            return scalar;
        }
        if (read.Value.Kind == NativeKind.Decimal && DecimalRefusal(read.Value.Decimal, column) is { } refused)
        {
            return refused;
        }
        return Project(read.Value) is { } value ? CallResult.Of(value) : CallResult.Fail(Failure.NotImplemented($"Field.Value of a {read.Value.Kind} value"));
    }

    /// <summary>
    /// The VARIANT of a native value (R-ORACLE-1 FMT1-FAMILIES), first the kinds that own no resource, written inline to
    /// the caller's slot without an allocation (task 6p9q): Null, Yes/No BOOL, Byte UI1, Integer I2, Long I4, Currency
    /// CY, Single R4, Double R8, Date DATE. The others are in <see cref="Project"/>.
    /// </summary>
    private static CallResult? Scalar(NativeValue v) => v.Kind switch
    {
        NativeKind.Null => CallResult.OfScalar(VarType.Null, 0),
        NativeKind.Boolean => CallResult.OfScalar(VarType.Bool, v.Boolean ? 0xFFFF : 0),
        NativeKind.Byte => CallResult.OfScalar(VarType.UI1, (byte)v.Integer),
        NativeKind.Integer => CallResult.OfScalar(VarType.I2, (ushort)(short)v.Integer),
        NativeKind.Long => CallResult.OfScalar(VarType.I4, (uint)(int)v.Integer),
        NativeKind.Currency => CallResult.OfScalar(VarType.Currency, v.Integer),
        NativeKind.Single => CallResult.OfScalar(VarType.R4, (uint)v.Bits),
        NativeKind.Double => CallResult.OfScalar(VarType.R8, (long)v.Bits),
        NativeKind.DateTime => CallResult.OfScalar(VarType.Date, (long)v.Bits),
        _ => null,
    };

    /// <summary>
    /// The owned VARIANT of a native value that holds a resource (R-ORACLE-1 FMT1-FAMILIES): Binary and OLE a UI1 array
    /// (an empty one reads Null), GUID the BSTR "{guid {…}}". Text and Memo BSTRs are built directly in
    /// <see cref="Value"/>.
    /// </summary>
    private static OwnedVariant? Project(NativeValue v) => v.Kind switch
    {
        NativeKind.Decimal => OwnedVariant.FromDecimal(new DecimalValue(v.Scale, v.Decimal.Negative, v.Decimal.High32, v.Decimal.Low64)),
        NativeKind.Binary or NativeKind.LongBinary => v.Bytes is { Length: > 0 } bytes ? ByteArray(bytes) : OwnedVariant.Null(),
        NativeKind.Guid => OwnedVariant.FromText("{guid " + new Guid(v.Bytes!).ToString("B").ToUpperInvariant() + "}"),
        // An edit buffer's text cell: the owned candidate the frame keeps (a stored row's text is read in Value).
        NativeKind.Text when v.Text is { } text => OwnedVariant.FromText(text),
        _ => null,
    };

    /// <summary>
    /// When a Decimal does not read as VT_DECIMAL (R-ORACLE-1 OR-WIRE, T-INT-34). A column of precision 38, which DAO
    /// CreateField(name, 20) makes, fails every non-Null read with 3000 "Reserved error (-1038)" and an IErrorInfo
    /// (r-dec-read/*-daofield-*); a precision from 29 to 37 is not observed; a value beyond the DECIMAL's 96 bits or scale
    /// 28 is not modelled. A calculated Decimal has no declared precision and reads as VT_DECIMAL
    /// (r-cplx-rebuild/calc-schema-change).
    /// </summary>
    private CallResult? DecimalRefusal(NativeDecimal value, ColumnDescriptor column)
    {
        if (column.CalculatedResultType is null && column.NumericPrecision > 28)
        {
            return column.NumericPrecision == 38
                ? Fail(3000, "Reserved error (-1038); there is no message for this error.", publishesErrorInfo: true)
                : CallResult.Fail(Failure.NotImplemented($"Field.Value of a Decimal column of precision {column.NumericPrecision} (not observed)"));
        }
        return value.FitsAutomation ? null : CallResult.Fail(Failure.NotImplemented($"Field.Value of a Decimal beyond VT_DECIMAL ({value.Magnitude} at scale {value.Scale})"));
    }

    private static OwnedVariant ByteArray(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return OwnedVariant.TakeArray(array, VarType.UI1);
    }

    protected override void OnFinalRelease() => _fields.Released(_ordinal);

    /// <summary>
    /// Parked by the Fields collection after the final release, to be handed out again (task hdw6). The hold on the
    /// collection goes after the parking: if it was the collection's last reference, the collection discards this
    /// wrapper with the others, and it reports kept, so it is not freed twice.
    /// </summary>
    protected override bool Park()
    {
        bool kept = _fields.Park(_ordinal, this);
        _fields.Release();
        return kept;
    }

    /// <summary>A parked wrapper handed out as a new Field: the construction reference and its hold on the collection.</summary>
    public FieldObject Reuse()
    {
        Revive();
        _fields.AddRef();
        return this;
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
        int? ordinal = CollectionLookup.Find(index, new CollectionNames([.. errors.Select(e => e.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))]), out bool objectIndex);
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

/// <summary>How OpenRecordset admits an Options value.</summary>
internal enum OptionAdmission
{
    Admitted,

    /// <summary>ACE refuses it with 3001 "Invalid argument." (no IErrorInfo).</summary>
    InvalidArgument,

    /// <summary>ACE accepts it with an effect CalcDb does not model yet.</summary>
    NotModelled,
}

/// <summary>
/// OpenRecordset Options (RecordsetOptionEnum) as ACE answers them on a local table, each bit asked alone and in the
/// combinations 3, 12, 48 and 260, on all four types, read-only and read-write (r-cursor-1b/option-flags-by-type,
/// r-fix-cursor-dao/option-flags-rw; R-CURSOR-1 CUR-PROV):
/// <list type="bullet">
/// <item>3001: dbRunAsync 1024, dbExecDirect 2048 and dbInconsistent|dbConsistent 48 on every type; dbSQLPassThrough 64
/// on dynaset, snapshot and forward-only (accepted on table-type, read-only open);</item>
/// <item>Updatable: False on a read-only open; table-type False with dbReadOnly 4; dynaset False with 4 or dbForwardOnly
/// 256; snapshot and forward-only always False;</item>
/// <item>Bookmarkable: forward-only False; dynaset and snapshot False with 256; table-type True;</item>
/// <item>dbAppendOnly 8 empties a dynaset (EOF, RecordCount 0) and changes nothing on the other types: that empty
/// population is not modelled yet.</item>
/// </list>
/// dbDenyWrite 1, dbDenyRead 2, dbInconsistent 16, dbConsistent 32, dbFailOnError 128 and dbSeeChanges 512 change none of
/// the members those records read (Type, Updatable, Bookmarkable, EOF, RecordCount); their effects on other sessions and
/// on writes are not modelled here (W-10, W-05).
/// </summary>
internal static class RecordsetOptions
{
    private const int DenyWrite = 1, DenyRead = 2, ReadOnly = 4, AppendOnly = 8, Inconsistent = 16, Consistent = 32, SqlPassThrough = 64,
        FailOnError = 128, ForwardOnly = 256, SeeChanges = 512, RunAsync = 1024, ExecDirect = 2048;

    private const int Known = DenyWrite | DenyRead | ReadOnly | AppendOnly | Inconsistent | Consistent | SqlPassThrough | FailOnError | ForwardOnly | SeeChanges | RunAsync | ExecDirect;

    public static OptionAdmission Admit(int type, int options)
    {
        if ((options & ~Known) != 0)
        {
            return OptionAdmission.NotModelled;
        }
        if ((options & (RunAsync | ExecDirect)) != 0 || (options & (Inconsistent | Consistent)) == (Inconsistent | Consistent)
            || (type != 1 && (options & SqlPassThrough) != 0))
        {
            return OptionAdmission.InvalidArgument;
        }
        if (type == 1 && (options & SqlPassThrough) != 0)
        {
            return OptionAdmission.NotModelled; // accepted read-only; not asked on a writable open
        }
        return type == 2 && (options & AppendOnly) != 0 ? OptionAdmission.NotModelled : OptionAdmission.Admitted;
    }

    public static bool Updatable(int type, int options, bool readOnlyDatabase) => !readOnlyDatabase && type switch
    {
        1 => (options & ReadOnly) == 0,
        2 => (options & (ReadOnly | ForwardOnly)) == 0,
        _ => false,
    };

    public static bool Bookmarkable(int type, int options) => type switch
    {
        1 => true,
        8 => false,
        _ => (options & ForwardOnly) == 0,
    };
}