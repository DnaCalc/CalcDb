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
        // Index of a dynaset, snapshot or forward-only recordset is 3251 without an IErrorInfo (r-dao-1b/recordset-members-dynaset, -snapshot, -forward-only, -at-eof).
        .Get("Index", (self, args) => Live(self, r => r.Cursor is TableCursor t ? Text(t.Index?.Name ?? "") : r.Fail(3251, DaoErrors.NotSupportedForType)))
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
        .Put("LockEdits", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetLockEdits(value)); })
        .Get("Transactions", (self, args) => Live(self, r => Bool(r.RecordsetType == 1)))
        // Restartable: table-type False, the other types True (r-dao-1b/recordset-members-table, -dynaset, -snapshot, -forward-only).
        .Get("Restartable", (self, args) => Live(self, r => Bool(r.RecordsetType != 1)))
        .Get("EditMode", (self, args) => Live(self, r => Int16((short)(r._frame?.Mode ?? EditMode.None))))
        .Get("Filter", (self, args) => Live(self, r => r.NotTableType() ?? Text(r._filter)))
        .Put("Filter", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetText(ref r._filter, value)); })
        .Get("Sort", (self, args) => Live(self, r => r.NotTableType() ?? Text(r._sort)))
        .Put("Sort", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetText(ref r._sort, value)); })
        .Get("CacheSize", (self, args) => Live(self, r => r.NotTableType() ?? Int32(r._cacheSize)))
        .Put("CacheSize", (self, args) => { nint value = args[^1]; return Live(self, r => r.NotTableType() ?? r.SetCacheSize(value)); })
        .Get("CacheStart", (self, args) => Live(self, r => r.NotTableType() ?? (r._cacheStart is { } start ? Bytes(start) : NullArray)))
        // hStmt (ODBCDirect) is 3251 on every local recordset (r-dao-1b/recordset-members-dynaset, -snapshot, -forward-only, -at-eof).
        .Get("hStmt", (self, args) => Live(self, r => r.Fail(3251, DaoErrors.NotSupportedForType)))
        .Put("CacheStart", (self, args) => { nint value = args[^1]; return Live(self, r => r.SetCacheStart(value)); })
        .Method("FillCache", (self, args) => { nint rows = args.Length > 0 ? args[0] : 0, start = args.Length > 1 ? args[1] : 0; return Live(self, r => r.FillCache(rows, start)); })
        .Method("GetRows", (self, args) => { nint count = args.Length > 0 ? args[0] : 0; return Live(self, r => r.GetRows(count)); })
        .Get("Collect", (self, args) => { nint item = args[0]; return Live(self, r => r.Collect(item)); })
        .Put("Collect", (self, args) => Live(self, r => SetCollect()))
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
        .Get("Properties", (self, args) => Live(self, r => r.PropertiesCollection()))
        .Get("LastModified", (self, args) => Live(self, r => r.LastModified()));

    private FieldsObject? _fields;
    private PropertiesObject? _properties;
    private readonly CatalogEntry _entry;
    private readonly int _options;
    private bool _pessimistic;
    private readonly ExplicitFrame[] _openLevels;
    private EditFrame? _frame;
    private RecordsetAfterRollback _afterRollback;
    private string? _notRecorded;
    private LastModifiedMark _lastModified;
    private byte[]? _lastModifiedRow;
    private string _filter = "", _sort = "";
    private int _cacheSize;
    private byte[]? _cacheStart;

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
        _openLevels = [.. database.Workspace.Transactions.Frames];
        database.AddRef(); // a recordset keeps its Database object alive
    }

    /// <summary>Updatable (R-CURSOR-1 CUR-PROV with the option matrix of r-fix-cursor-dao/option-flags-rw): <see cref="RecordsetOptions.Updatable"/>.</summary>
    public bool Updatable => RecordsetOptions.Updatable(RecordsetType, _options, Database.ReadOnly);

    /// <summary>
    /// A table-type or dynaset recordset that dbReadOnly alone makes not updatable, on a writable open
    /// (r-cursor-1b/option-readonly-write: Edit, AddNew and Delete 3027 from DAO.Recordset without an IErrorInfo, a Value
    /// put from DAO.Field with one, 3027 on a dynaset and 3020 on table-type, Update and CancelUpdate 3020, no edit being
    /// pending).
    /// </summary>
    public bool ReadOnlyByOption => RecordsetType is 1 or 2 && !Database.ReadOnly && RecordsetOptions.ReadOnlyOnly(_options);

    /// <summary>The table's property blob (CAT-P), read once; null with <paramref name="failure"/> when it cannot be read.</summary>
    public PropertyBlob? Blob(out string? failure)
    {
        if (!_blobRead)
        {
            _blob = Database.Blob(_entry, out _blobFailure);
            _blobRead = true;
        }
        failure = _blobFailure;
        return _blob;
    }

    private PropertyBlob? _blob;
    private string? _blobFailure;
    private bool _blobRead;

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
        if (!OwnedVariant.TryChangeScalar(v, target.Value, SessionLocale.Lcid, out NativeVariant changed, out _))
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
        _context ??= new ExpressionContext(SessionLocale.Lcid, TextCompareMode.Database, Database.Collation, ExpressionUse.Find);
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
            refused = ReadOnlyByOption
                ? Fail(MutationError.ReadOnly, "Cannot update. Database or object is read-only.")
                : CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ReadOnlyRecordset", "an edit member of a table-type or dynaset recordset that is not updatable (dbForwardOnly or a read-only Database): ACE's errors are not recorded"));
            return null;
        }
        if (Database.EditContext(_entry, out string? failure) is not { } context)
        {
            refused = CallResult.Fail(Failure.NotImplemented("an edit member when the table's edit semantics cannot be compiled: " + failure));
            return null;
        }
        _frame = new EditFrame(context, Cursor, Transactions, updatable: RecordsetType is 1 or 2) { LockEdits = _pessimistic };
        return _frame;
    }

    /// <summary>
    /// LockEdits put (R-CURSOR-1 CUR-EDIT conflicts; TXN-LOCAL): False makes the next Edit optimistic (the page is locked
    /// at Update, with the row-image check and its one 3197), True pessimistic (the page is locked at Edit); the edit
    /// frame takes it (<see cref="EditFrame.LockEdits"/>). Recorded on a dynaset before any edit (r-cursor-1/edit-conflict-
    /// optimistic: LockEdits = False). A put on a snapshot or forward-only recordset, or with an edit pending, is not
    /// recorded and is named.
    /// </summary>
    private CallResult SetLockEdits(nint value)
    {
        if (RecordsetType is not (1 or 2))
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsType", $"LockEdits put on a type {RecordsetType} recordset (not recorded)"));
        }
        if (_frame is { Mode: not EditMode.None })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.LockEditsDuringEdit", "LockEdits put with an edit pending (not recorded)"));
        }
        if (Args.Bool(value, omitted: true) is not bool pessimistic)
        {
            return CallResult.Fail(Failure.NotImplemented("LockEdits put of a value that is not Boolean"));
        }
        _pessimistic = pessimistic;
        if (_frame is { } frame)
        {
            frame.LockEdits = pessimistic;
        }
        return CallResult.Nothing;
    }

    /// <summary>
    /// 3251 without an IErrorInfo on a table-type recordset, for Filter, Sort, CacheSize and CacheStart, gets and puts
    /// (r-cursor-1b/filter-sort-other-types, cache-size-properties; r-dao-1b/recordset-members-table); null otherwise.
    /// </summary>
    private CallResult? NotTableType() => RecordsetType == 1 ? Fail(3251, DaoErrors.NotSupportedForType) : null;

    /// <summary>
    /// A Filter or Sort put on a dynaset, snapshot or forward-only recordset keeps the text and changes nothing in the
    /// recordset; a text naming no column is accepted and fails only at the derived OpenRecordset (qualified.md R-CURSOR-1
    /// CUR-LIFE; r-cursor-1/life-filter-sort, r-cursor-1b/filter-sort-other-types, r-def-cursor-dao/err-badcol-filter).
    /// </summary>
    private CallResult SetText(ref string kept, nint value)
    {
        kept = Args.Text(value) ?? "";
        return CallResult.Nothing;
    }

    /// <summary>
    /// CacheSize put: any Long is kept and read back (0, 1, 4, 5, 6, 100, 1200, 1201, -1; "10" and 10.5 as 10, the binder's
    /// coercion; Null 3421 with an IErrorInfo there): r-cursor-1b/cache-size-properties on dynaset, snapshot and
    /// forward-only. CacheStart reads a null array whatever was put.
    /// </summary>
    private CallResult SetCacheSize(nint value)
    {
        _cacheSize = Args.Int32(value, omitted: 0) ?? 0;
        return CallResult.Nothing;
    }

    /// <summary>
    /// CacheStart put (r-cursor-1b/cache-fill-and-start, dynaset): a Byte array is kept and read back as given (00000000,
    /// 02020000), an empty one and BSTR "" make it read a null array again; I4, Null and Empty are 3421 with an IErrorInfo
    /// and keep what was read. Table-type, a BSTR with characters and other value types are not recorded.
    /// </summary>
    private CallResult SetCacheStart(nint value)
    {
        if (RecordsetType == 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.CacheStartPutType", "CacheStart put on a table-type recordset (not recorded)"));
        }
        VarType vt = Args.TypeOf(value);
        NativeVariant* v = Args.Cell(value);
        if (vt == (VarType.Array | VarType.UI1) && v->Pointer != 0 && SafeArrays.Shape(v->Pointer) is [(int lower, int count)])
        {
            _cacheStart = count <= 0 ? null : new Span<byte>(SafeArrays.ElementPointer(v->Pointer, lower), count).ToArray();
            return CallResult.Nothing;
        }
        if (vt == VarType.Bstr && (v->Pointer == 0 || Bstrs.Chars(v->Pointer).Length == 0))
        {
            _cacheStart = null;
            return CallResult.Nothing;
        }
        return vt is VarType.I4 or VarType.Null or VarType.Empty
            ? Fail(3421, DaoErrors.ConversionError, publishesErrorInfo: true)
            : CallResult.Fail(Failure.NotModelled("NotModelled.CacheStartPutValue", $"CacheStart put of a {vt} value (not recorded)"));
    }

    /// <summary>
    /// FillCache(Rows, StartBookmark) changes nothing a member reads (r-cursor-1b/cache-fill-and-start: BOF, EOF, the
    /// value and RecordCount stay; dynaset, snapshot and forward-only): Rows omitted or any Long (0, -1, 21, 1000),
    /// StartBookmark omitted, a Byte array or a BSTR. A Rows or StartBookmark that does not convert (BSTR "a", Null) is
    /// 3421 without an IErrorInfo; table-type is 3219 (CUR-BULK).
    /// </summary>
    private CallResult FillCache(nint rows, nint start)
    {
        if (RecordsetType == 1)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (rows != 0 && !OwnedVariant.TryChangeScalar(Args.Cell(rows), VarType.I4, SessionLocale.Lcid, out _, out _))
        {
            return Fail(3421, DaoErrors.ConversionError);
        }
        if (start == 0)
        {
            return CallResult.Nothing;
        }
        VarType vt = Args.TypeOf(start);
        return vt is (VarType.Array | VarType.UI1) or VarType.Bstr ? CallResult.Nothing
            : vt == VarType.Null ? Fail(3421, DaoErrors.ConversionError)
            : CallResult.Fail(Failure.NotModelled("NotModelled.FillCacheStart", $"FillCache with a StartBookmark of type {vt} (not recorded)"));
    }

    /// <summary>
    /// GetRows(NumRows) (qualified.md R-CURSOR-1 CUR-BULK): a VT_ARRAY|VT_VARIANT [0+fields, 0+rows], dimension 1 the
    /// field, of up to NumRows rows from the current one, each read as Field.Value reads it; the cursor ends after the last
    /// row read (EOF when the rows ran out, r-cursor-1b/getrows-beyond-eof: GetRows(21) of 20 rows returns 20). NumRows
    /// omitted or Missing is 1; it converts as a Long ("3", 2.5 as 2, I2), 0, -1, True and Empty are 3001, Null and "abc"
    /// 3421, all from DAO.Recordset without an IErrorInfo (getrows-count-arguments). At EOF, or on an empty recordset, 3021.
    /// A pending Edit or AddNew gives the first row from its buffer and is discarded (getrows-edit-buffer). A current row another call deleted gives ACE's degenerate
    /// array, which is named (its shape is not identified beyond one record).
    /// </summary>
    private CallResult GetRows(nint countArg)
    {
        int count = 1;
        if (countArg != 0)
        {
            if (!OwnedVariant.TryChangeScalar(Args.Cell(countArg), VarType.I4, SessionLocale.Lcid, out NativeVariant converted, out _))
            {
                return Fail(3421, DaoErrors.ConversionError);
            }
            count = (int)converted.Int64;
            if (count <= 0)
            {
                return Fail(3001, DaoErrors.InvalidArgument);
            }
        }
        if (UnpublishedRead("GetRows") is { } unpublished)
        {
            return unpublished;
        }
        if (Cursor.Eof)
        {
            return Fail(3021, DaoErrors.NoCurrentRecord);
        }
        if (Cursor.Bof)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsAtBof", "GetRows with the cursor before the first row (not recorded)"));
        }
        IReadOnlyList<ColumnDescriptor> columns = Cursor.Table.Columns;
        var cells = new List<OwnedVariant>();
        try
        {
            int rows = 0;
            while (rows < count && !Cursor.Eof)
            {
                foreach (ColumnDescriptor column in columns)
                {
                    // With an edit pending the first row is its buffer (getrows-edit-buffer: an Edit's assigned 99.5 and
                    // "edited memo"; an AddNew's 7, 7.5, "added" and Nulls in place of the current row 1, then rows 2 and 3).
                    CallResult cell = rows == 0 && _frame is { Mode: not EditMode.None } ? FieldObject.ReadValue(this, column) : FieldObject.StoredValue(this, column);
                    if (cell.Failure is { } failure)
                    {
                        return failure.Number == 3167
                            ? CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsDeletedRow", "GetRows on a row another call deleted: ACE's degenerate array (CUR-BULK) is not identified"))
                            : CallResult.Fail(Failure.NotModelled("NotModelled.GetRowsCell", "GetRows of a cell Field.Value refuses: " + failure.Description));
                    }
                    cells.Add(Owned(cell));
                }
                _frame?.Discard(); // the move discards the edit (EditMode 0 after, CancelUpdate 3020)
                rows++;
                if (Cursor.MoveNext() is not (MoveOutcome.Moved or MoveOutcome.ReachedBoundary))
                {
                    break;
                }
            }
            nint array = SafeArrays.Create(VarType.Variant, (0, columns.Count), (0, rows));
            NativeVariant* data = rows == 0 || columns.Count == 0 ? null : (NativeVariant*)SafeArrays.ElementPointer(array, 0, 0);
            for (int i = 0; i < cells.Count; i++)
            {
                cells[i].TransferTo(data + i); // dimension 1 (the field) varies fastest
            }
            return CallResult.Of(OwnedVariant.TakeArray(array, VarType.Variant));
        }
        finally
        {
            foreach (OwnedVariant cell in cells)
            {
                cell.Dispose();
            }
        }
    }

    /// <summary>A successful read's value as an owned VARIANT.</summary>
    private static OwnedVariant Owned(CallResult cell) => cell.Value ?? OwnedVariant.Adopt(cell.Scalar);

    /// <summary>
    /// Collect(Item): the value of the field the index names, as Field.Value reads it, without a Field object; an index
    /// naming no field (3, -1, "T.N", Null, Empty) is 3265 from DAO.Fields without an IErrorInfo; names compare
    /// case-insensitively (r-def-cursor-dao/recordset-collect-member).
    /// </summary>
    private CallResult Collect(nint item) => CollectColumn(item, out CallResult refused) is { } column ? FieldObject.ReadValue(this, column) : refused;

    /// <summary>
    /// Collect(Item) = value on a table-type, dynaset, snapshot or forward-only recordset of a table: not recorded (the
    /// one record, r-def-cursor-dao/recordset-collect-member, puts through a SELECT dynaset), so it is named.
    /// </summary>
    private static CallResult SetCollect() =>
        CallResult.Fail(Failure.NotModelled("NotModelled.CollectPut", "Collect put on a recordset of a table (recorded on a SELECT dynaset only)"));

    private ColumnDescriptor? CollectColumn(nint item, out CallResult refused)
    {
        refused = default;
        int? ordinal = CollectionLookup.Find(item, FieldNames, out bool objectIndex);
        if (objectIndex)
        {
            refused = CallResult.Fail(Failure.NotModelled("NotModelled.CollectObjectIndex", "Collect with an object index (not recorded)"));
            return null;
        }
        if (ordinal is not int i)
        {
            refused = CallResult.Fail(Failure.Dao(3265, "DAO.Fields", DaoErrors.ItemNotFound));
            return null;
        }
        return Cursor.Table.Columns[i];
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
        EditOutcome outcome = frame.AddNew();
        if (outcome.Succeeded && frame.Table.Counter is { Last: > 0 and <= uint.MaxValue } publishedCounter)
        {
            Database.Engine.Changes.MarkAutoNumberPublished(Database.Name, _entry.Name, (uint)publishedCounter.Last);
        }
        return EditPublication.Of(outcome);
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
            return ReadOnlyByOption ? NoEditPending : refused;
        }
        int type = Args.Int32(typeArg, omitted: 1) ?? 1;
        bool force = Args.Bool(forceArg, omitted: false) ?? false;
        if (frame.Updatable && frame.Mode != EditMode.None && UnpublishedRead("Update", frame.Table.Semantics) is { } unpublished)
        {
            return unpublished;
        }
        EditMode mode = frame.Mode;
        UpdateOutcome outcome = frame.Update(type, force);
        CallResult result = EditPublication.Of(outcome);
        if (outcome.Error?.Number == MutationError.Duplicate && mode == EditMode.AddNew && frame.Table.Counter is { Last: > 0 and <= uint.MaxValue } refusedCounter)
        {
            Database.Engine.Changes.MarkAutoNumberPublished(Database.Name, _entry.Name, (uint)refusedCounter.Last);
        }
        if (result.Failure is null && frame.Mode == EditMode.None)
        {
            if (outcome.Change is { } change && Database.Workspace.Transactions.Depth == 0)
            {
                Database.Engine.Changes.MarkPublished([change]);
            }
            Modified(mode, outcome);
        }
        return result;
    }

    /// <summary>
    /// LastModified after an accepted Update: an Edit's row is the current row, which Update keeps (CUR-EDIT), so its
    /// bookmark is the current one (r-cursor-1/edit-cancel-rollback: LastModified equals Bookmark after the Update). A
    /// published AddNew row gets its landing root bookmark on a table-type cursor, or its appended keyset bookmark on a
    /// fully populated dynaset; otherwise LastModified stays named.
    /// </summary>
    private void Modified(EditMode mode, UpdateOutcome outcome)
    {
        if (mode == EditMode.Edit && Cursor.GetBookmark(out _) is { } bookmark)
        {
            (_lastModified, _lastModifiedRow) = (LastModifiedMark.Row, bookmark);
        }
        else if (mode == EditMode.AddNew && outcome.Change?.Inserted is { } inserted)
        {
            byte[]? added = Cursor switch
            {
                TableCursor => TableBookmark.Encode(inserted),
                PopulationCursor pop when outcome.InsertedOrdinal is int ord => pop.BookmarkForOrdinal(ord),
                _ => null,
            };
            (_lastModified, _lastModifiedRow) = added is not null ? (LastModifiedMark.Row, added) : (LastModifiedMark.NewRow, null);
        }
        else
        {
            (_lastModified, _lastModifiedRow) = (mode == EditMode.AddNew ? LastModifiedMark.NewRow : LastModifiedMark.None, null);
        }
    }

    /// <summary>
    /// LastModified (VT_ARRAY|VT_UI1): the bookmark of the row the last accepted Edit Update changed; after an outermost
    /// Rollback a null array (TXN-CLOSE: r-cursor-1/edit-rollback-*, edit-cancel-rollback; CommitTrans keeps it,
    /// edit-commit-opened-inside). Before any Update a table-type or dynaset recordset answers a null array and a snapshot
    /// or forward-only one 3251 (r-dao-1b/recordset-members-table, -dynaset, -at-eof, -snapshot, -forward-only). After an
    /// AddNew it is not identified and is named.
    /// </summary>
    private CallResult LastModified() => _lastModified switch
    {
        LastModifiedMark.Row => Bytes(_lastModifiedRow!),
        LastModifiedMark.Cleared => NullArray,
        LastModifiedMark.NewRow => CallResult.Fail(Failure.NotModelled("NotModelled.LastModifiedNewRow", "LastModified after an AddNew Update: the new row's bookmark (W-05/growth-structural-closure)")),
        _ => RecordsetType is 1 or 2 ? NullArray : Fail(3251, DaoErrors.NotSupportedForType),
    };

    /// <summary>VT_ARRAY|VT_UI1 with a null SAFEARRAY, as ACE answers LastModified after a Rollback.</summary>
    private static CallResult NullArray => CallResult.Of(OwnedVariant.TakeArray(0, VarType.UI1));

    private static CallResult Bytes(byte[] bytes)
    {
        nint array = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
        bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(array, 0), bytes.Length));
        return CallResult.Of(OwnedVariant.TakeArray(array, VarType.UI1));
    }

    /// <summary>How this recordset stands after a Rollback of its Workspace (TXN-CLOSE); Valid until one reaches it.</summary>
    public RecordsetAfterRollback AfterRollback => _afterRollback;

    /// <summary>
    /// A Rollback of <paramref name="level"/> (qualified.md R-TXN-1 TXN-CLOSE, the recordset after the resolution). The
    /// outermost Rollback makes a table-type or dynaset recordset opened inside the level <see cref="RecordsetAfterRollback.Invalid"/>
    /// and a dynaset opened outside every level <see cref="RecordsetAfterRollback.LostCurrentRecord"/>; both answer
    /// LastModified with a null array. A snapshot or forward-only recordset opened inside the level, and any recordset
    /// opened inside an inner level that is rolled back, are not recorded (<see cref="RecordsetAfterRollback.NotRecorded"/>).
    /// A recordset opened before the level keeps its state in the cases the records do not cover (a table-type,
    /// snapshot or forward-only one opened outside every level; any one at an inner Rollback). CommitTrans changes nothing
    /// (edit-commit-opened-inside: the recordset reads on its row).
    /// </summary>
    public void RolledBack(ExplicitFrame level, bool outermost)
    {
        if (Closed || _afterRollback is RecordsetAfterRollback.Invalid or RecordsetAfterRollback.NotRecorded)
        {
            return;
        }
        if (Array.IndexOf(_openLevels, level) >= 0)
        {
            if (outermost && RecordsetType is 1 or 2)
            {
                _afterRollback = RecordsetAfterRollback.Invalid;
                _lastModified = LastModifiedMark.Cleared;
            }
            else
            {
                _afterRollback = RecordsetAfterRollback.NotRecorded;
                _notRecorded = outermost
                    ? $"a type {RecordsetType} recordset opened inside a level that was rolled back (TXN-CLOSE records table-type and dynaset)"
                    : $"a recordset opened inside an inner level ({level.Level}) that was rolled back (TXN-CLOSE records the outermost level)";
            }
        }
        else if (outermost && RecordsetType == 2 && _openLevels.Length == 0)
        {
            _afterRollback = RecordsetAfterRollback.LostCurrentRecord;
            _lastModified = LastModifiedMark.Cleared;
        }
    }

    /// <summary>A recordset that a Rollback changed answers only what the records show (TXN-CLOSE); every other member is named.</summary>
    protected override Operation? Find(MemberDecl member) => _afterRollback switch
    {
        RecordsetAfterRollback.Valid => base.Find(member),
        RecordsetAfterRollback.Invalid => InvalidOperations.Find(member) ?? AfterRollbackNamed(member),
        RecordsetAfterRollback.LostCurrentRecord => LostCurrentOperations.Find(member) ?? AfterRollbackNamed(member),
        _ => member.Name == "Close" ? base.Find(member) : AfterRollbackNamed(member),
    };

    private Operation AfterRollbackNamed(MemberDecl member)
    {
        string detail = $"Recordset.{member.Name} after a Rollback: {_notRecorded ?? (_afterRollback == RecordsetAfterRollback.Invalid ? "a recordset the Rollback made invalid" : "a dynaset that lost its current record")}, not recorded (TXN-CLOSE)";
        return (self, args) => CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", detail));
    }

    /// <summary>
    /// A table-type or dynaset recordset opened inside the level the outermost Rollback ended (r-cursor-1/edit-rollback-no-change,
    /// edit-rollback-cancel-only, edit-rollback-table, edit-cancel-rollback): EditMode 0; Move 0, MoveFirst and Bookmark
    /// 3420 from DAO.Recordset without an IErrorInfo; LastModified a null array; Close succeeds.
    /// </summary>
    private static readonly OperationTable InvalidOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Method("Move", (self, args) => Args.Int32(args[0], omitted: null) == 0 && (args.Length < 2 || args[1] == 0) ? ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid) : ((RecordsetObject)self).AfterRollbackNamed(Decl.Member("Move", InvokeKind.Method))(self, args))
        .Method("MoveFirst", (self, args) => ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid))
        .Get("Bookmark", (self, args) => ((RecordsetObject)self).Fail(3420, DaoErrors.ObjectInvalid))
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>
    /// A dynaset opened outside every level, after the outermost Rollback (r-cursor-1/edit-rollback-opened-before): EditMode
    /// 0; Move 0 3021 from DAO.Recordset without an IErrorInfo; MoveFirst repositions and the recordset is valid again;
    /// LastModified a null array; Close succeeds.
    /// </summary>
    private static readonly OperationTable LostCurrentOperations = new OperationTable(Decl)
        .Get("EditMode", (self, args) => Int16(0))
        .Method("Move", (self, args) => Args.Int32(args[0], omitted: null) == 0 && (args.Length < 2 || args[1] == 0) ? ((RecordsetObject)self).Fail(3021, DaoErrors.NoCurrentRecord) : ((RecordsetObject)self).AfterRollbackNamed(Decl.Member("Move", InvokeKind.Method))(self, args))
        .Method("MoveFirst", (self, args) =>
        {
            var r = (RecordsetObject)self;
            r._afterRollback = RecordsetAfterRollback.Valid;
            return Operations.Find(Decl.Member("MoveFirst", InvokeKind.Method))!(self, args);
        })
        .Get("LastModified", (self, args) => NullArray)
        .Method("Close", (self, args) => Live(self, r => r.Close()));

    /// <summary>CancelUpdate(UpdateType): dbUpdateRegular drops the pending edit without Rollback (CUR-EDIT); another type is not recorded.</summary>
    private CallResult CancelUpdate(nint typeArg)
    {
        if (Args.Int32(typeArg, omitted: 1) is not 1)
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.Edit.CancelUpdateType", "CancelUpdate with an UpdateType other than dbUpdateRegular (batch updates are not recorded)"));
        }
        return Frame(out CallResult refused) is { } frame ? EditPublication.Of(frame.CancelUpdate()) : ReadOnlyByOption ? NoEditPending : refused;
    }

    /// <summary>3020 from DAO.Recordset: Update or CancelUpdate of a recordset no edit can be pending on (option-readonly-write).</summary>
    private CallResult NoEditPending => Fail(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");

    /// <summary>Delete: discards any pending Edit or AddNew and removes the current row (W-05/growth-structural-closure; CUR-EDIT, CUR-POS).</summary>
    private CallResult Delete()
    {
        if (Frame(out CallResult refused) is not { } frame)
        {
            return refused;
        }
        if (frame.Updatable && UnpublishedRead("Delete", frame.Table.Semantics) is { } unpublished)
        {
            return unpublished;
        }
        return EditPublication.Of(frame.Delete());
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
    /// AbsolutePosition: the zero-based ordinal on a dynaset or snapshot, or -1 at BOF, EOF, empty, or after own Delete
    /// (CUR-POS); forward-only refuses it with 3219 and table-type with 3251 (r-cursor-1/pos-moves-dynaset,
    /// pos-moves-forwardonly, pop-count-table).
    /// </summary>
    private CallResult AbsolutePosition() => Cursor switch
    {
        TableCursor => Fail(3251, DaoErrors.NotSupportedForType),
        PopulationCursor { ForwardOnly: true } => Fail(3219, DaoErrors.InvalidOperation),
        PopulationCursor { Ordinal: int ordinal } => Int32(ordinal),
        PopulationCursor => Int32(-1),
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
        if (!OwnedVariant.TryChangeScalar(Args.Cell(value), VarType.R4, SessionLocale.Lcid, out NativeVariant changed, out _))
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
        // A forward-only recordset is not bookmarkable: 3219 from DAO.Recordset with no IErrorInfo
        // (r-dao-1b/recordset-members-forward-only, recordset-properties-forward-only; qualified.md CUR-ID).
        if (RecordsetType == 8)
        {
            return Fail(3219, DaoErrors.InvalidOperation);
        }
        if (!Bookmarkable)
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
        int depth = Database.Workspace.Transactions.Depth, openDepth = _openLevels.Length;
        if (RecordsetType != 1 && depth > openDepth)
        {
            return RecordsetType == 2 && openDepth == 0 && depth == 1
                ? Fail(3246, "Operation not supported in transactions.")
                : CallResult.Fail(Failure.NotModelled("NotModelled.Txn.CloseRecordsetOpenedOutsideLevel", $"Close of a type {RecordsetType} recordset opened at depth {openDepth} and closed at depth {depth} (TXN-CLOSE records a dynaset from depth 0 to 1)"));
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
        return f.Recordset.Closed ? f.Recordset.Fail(3420, DaoErrors.ObjectInvalid) : f.Recordset.AfterRollback != RecordsetAfterRollback.Valid ? AfterRollbackNamed("Fields") : op(f);
    }

    /// <summary>A Fields or Field member of a recordset a Rollback changed, other than the Value get: not recorded (TXN-CLOSE).</summary>
    public static CallResult AfterRollbackNamed(string member) => CallResult.Fail(Failure.NotModelled("NotModelled.Txn.RecordsetAfterRollback", $"{member} of a recordset a Rollback changed, not recorded (TXN-CLOSE)"));

    private CallResult Item(nint index)
    {
        if (Recordset.AfterRollback != RecordsetAfterRollback.Valid)
        {
            return AfterRollbackNamed("Fields.Item");
        }
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
        .Put("ValidateOnSet", (self, args) => { nint value = args[^1]; return Live(self, f => f.SetValidateOnSet(value)); })
        // The members below follow r-dao-1b/recordset-fields-members-each (a dynaset of every family, read-only open),
        // r-fix-cursor-dao/updatable-members-rw and r-cursor-1b/option-readonly-write (writable opens).
        .Get("DataUpdatable", (self, args) => Live(self, f => f.DataUpdatable() is bool u ? Bool(u) : f.CalculatedNamed("DataUpdatable")))
        .Get("Attributes", (self, args) => Live(self, f => f.DataUpdatable() is bool u && ColumnFacts.Attributes(f.Column, u) is int a ? Int32(a) : f.CalculatedNamed("Attributes")))
        .Get("AllowZeroLength", (self, args) => Live(self, f => f.Stored("AllowZeroLength", Bool(false))))
        .Get("Required", (self, args) => Live(self, f => f.Stored("Required", Bool(false))))
        .Get("DefaultValue", (self, args) => Live(self, f => f.Stored("DefaultValue", Text(""))))
        .Get("ValidationRule", (self, args) => Live(self, f => f.Stored("ValidationRule", Text(""))))
        .Get("ValidationText", (self, args) => Live(self, f => f.Stored("ValidationText", Text(""))))
        .Get("CollatingOrder", (self, args) => Live(self, f => Int32(ColumnFacts.CollatingOrder(f.Column))))
        .Get("CollectionIndex", (self, args) => Live(self, f => Int16(f._ordinal)))
        .Get("SourceField", (self, args) => Live(self, f => Text(f.Column.Name)))
        .Get("SourceTable", (self, args) => Live(self, f => Text(f.Recordset.TableName)))
        .Get("Expression", (self, args) => Live(self, f => f.Column.CalculatedResultType is null ? Text("") : f.CalculatedNamed("Expression")))
        .Get("ComplexType", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("ForeignName", (self, args) => Live(self, f => f.Fail(3219, DaoErrors.InvalidOperation)))
        .Get("VisibleValue", (self, args) => Live(self, f => f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("OriginalValue", (self, args) => Live(self, f => f.Recordset.FrameIfMade is { Mode: not EditMode.None }
            ? CallResult.Fail(Failure.NotModelled("NotModelled.OriginalValueDuringEdit", "Field.OriginalValue with an edit pending (not recorded)"))
            : f.Fail(3251, DaoErrors.NotSupportedForType)))
        .Get("FieldSize", (self, args) => Live(self, f => f.FieldSize()))
        .Get("Properties", (self, args) => Live(self, f => f.PropertiesCollection()));

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
        return f.Recordset.Closed ? f.Fail(3420, DaoErrors.ObjectInvalid) : f.Recordset.AfterRollback != RecordsetAfterRollback.Valid ? FieldsObject.AfterRollbackNamed("Field") : op(f);
    }

    /// <summary>DataTypeEnum: the native codes 1-12 and 15 are DAO's own numbers (dbBoolean … dbMemo, dbGUID).</summary>
    private int? DaoType() => ColumnFacts.DaoType(Column);

    /// <summary>
    /// DataUpdatable: the recordset's Updatable, for the column kinds the records show. On a read-only open every stored
    /// family is False (r-dao-1b/recordset-fields-members-each: the 14 families of Typed, AutoNumber and Decimal included).
    /// On a writable open the records show Long, Text and Double columns (r-fix-cursor-dao/updatable-members-rw: Parent's
    /// Long, Text and Double, True on table-type and dynaset, False on snapshot and forward-only; r-cursor-1b/option-
    /// readonly-write: Long, True, False with dbReadOnly; r-def-cursor-dao/prov-heap-duplicate-rows: Long and Text, True).
    /// Any other kind, and a calculated column, is not recorded: null.
    /// </summary>
    private bool? DataUpdatable()
    {
        ColumnDescriptor column = Column;
        if (column.CalculatedResultType is not null)
        {
            return null;
        }
        if (Recordset.Database.ReadOnly)
        {
            return false;
        }
        return column.TypeCode is NativeTypes.Long or NativeTypes.Text or NativeTypes.Double && !column.IsAutoNumber ? Recordset.Updatable : null;
    }

    /// <summary>
    /// FieldSize (qualified.md R-STRING-1 STR-LV): a Memo's current value in bytes, twice its UTF-16 units, on every
    /// recordset type (r-dao-1b/recordset-fields-members-each: 2 for a one-character Memo, 0 for a Null OLE value); a
    /// field of any other type is 3259. A stored OLE value, a Null Memo, a long value during an edit and a read failure
    /// are not recorded and are named.
    /// </summary>
    private CallResult FieldSize()
    {
        ColumnDescriptor column = Column;
        if (column.TypeCode is not (NativeTypes.Memo or NativeTypes.Ole))
        {
            return Fail(3259, DaoErrors.InvalidFieldType);
        }
        if (Recordset.FrameIfMade is { Mode: not EditMode.None })
        {
            return CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeDuringEdit", "Field.FieldSize of a long value with an edit pending (not recorded)"));
        }
        if (Recordset.UnpublishedRead("Field.FieldSize") is { } unpublished)
        {
            return unpublished;
        }
        if (column.TypeCode == NativeTypes.Ole)
        {
            // A Null OLE value is 0 (recordset-fields-members-each: TOle); a stored one's length is not recorded.
            FieldResult read = Recordset.Cursor.Read(column);
            return read.Succeeded && read.Value.Kind == NativeKind.Null ? Int32(0)
                : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeOle", "Field.FieldSize of an OLE value that is not Null (not recorded)"));
        }
        TextFieldRead text = Recordset.Cursor.ReadText(column);
        return text.HasText ? Int32(text.Shape.Units * 2)
            : CallResult.Fail(Failure.NotModelled("NotModelled.FieldSizeNoText", $"Field.FieldSize of a Memo that reads no text ({text.Result.Failure?.ToString() ?? "Null"}; not recorded)"));
    }

    private CallResult CalculatedNamed(string member) =>
        CallResult.Fail(Failure.NotModelled("NotModelled.FieldKindMember",$"Field.{member} of the column {Column.Name} (native type {Column.TypeCode}) on a recordset: its kind is not recorded"));

    private PropertiesObject? _properties;

    /// <summary>A stored column property of the table's blob (CAT-P), or <paramref name="unset"/>.</summary>
    private CallResult Stored(string name, CallResult unset)
    {
        PropertyBlob? blob = Recordset.Blob(out string? failure);
        return failure is not null
            ? CallResult.Fail(Failure.NotImplemented("a Field member when the table's property blob cannot be read: " + failure))
            : ColumnFacts.Stored(blob, Column, name, unset);
    }

    /// <summary>
    /// The 24 built-ins of a field, then the column's other stored properties (r-dao-1b/recordset-fields-members-each:
    /// Count 24 on each field of a dynaset of a table; the list and order as a TableDef field's, r-dao-1b/field-properties-enumerated).
    /// </summary>
    private CallResult PropertiesCollection()
    {
        PropertyBlob? blob = Recordset.Blob(out string? failure);
        if (failure is not null)
        {
            return CallResult.Fail(Failure.NotImplemented("Field.Properties when the table's property blob cannot be read: " + failure));
        }
        if (DaoType() is not int type)
        {
            return CallResult.Fail(Failure.NotImplemented($"Field.Properties of native type {Column.TypeCode}"));
        }
        _properties ??= new PropertiesObject(this, BuiltInProperties.With(BuiltInProperties.TableDefField((short)type), blob?.Map(PropertySectionKind.ColumnValues, Column.Name)));
        return Object(_properties);
    }

    private int? Size() => ColumnFacts.Size(Column);

    private CallResult Value() => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : ReadValue(Recordset, Column);

    /// <summary>
    /// The Value get of <paramref name="column"/> of an open recordset (Field.Value, Recordset.Collect): after a Rollback
    /// as TXN-CLOSE shows it, during an edit the buffer cell, otherwise the current row's stored value.
    /// </summary>
    public static CallResult ReadValue(RecordsetObject recordset, ColumnDescriptor column)
    {
        switch (recordset.AfterRollback)
        {
            // TXN-CLOSE: from DAO.Field with an IErrorInfo (r-cursor-1/edit-rollback-no-change 3420, edit-rollback-opened-before 3021).
            case RecordsetAfterRollback.Invalid:
                return FieldFailure(3420, DaoErrors.ObjectInvalid);
            case RecordsetAfterRollback.LostCurrentRecord:
                return FieldFailure(3021, DaoErrors.NoCurrentRecord);
            case RecordsetAfterRollback.NotRecorded:
                return FieldsObject.AfterRollbackNamed("Field.Value");
        }
        if (recordset.FrameIfMade is { Mode: not EditMode.None } frame)
        {
            // During an edit the field reads its buffer cell: the accepted value, the stored original, or Null when unset (CUR-EDIT).
            return Published(frame.Read(column), column);
        }
        return recordset.UnpublishedRead("Field.Value") ?? StoredValue(recordset, column);
    }

    /// <summary>The current row's stored value of <paramref name="column"/>, with Field.Value's failures (no edit pending, the read admitted).</summary>
    public static CallResult StoredValue(RecordsetObject recordset, ColumnDescriptor column)
    {
        if (column.TypeCode is NativeTypes.Text or NativeTypes.Memo)
        {
            // D-STRING-1 (task n7mw): the final BSTR is built straight from the encoded field bytes.
            TextFieldRead text = recordset.Cursor.ReadText(column);
            if (text.HasText)
            {
                return CallResult.OfOwnedBstr(Bstrs.FromUnits(text.Shape.Units, text, static (dest, t) => t.DecodeInto(dest)));
            }
            return Published(text.Result, column);
        }
        return Published(recordset.Cursor.Read(column), column);
    }

    /// <summary>A failure from DAO.Field with an IErrorInfo, as Value failures are published (R-ERR-1 ERR-EXCEP).</summary>
    private static CallResult FieldFailure(int number, string description) =>
        CallResult.Fail(Failure.Dao(number, "DAO.Field", description) with { PublishesErrorInfo = true });

    private CallResult Assign(nint value) => Recordset.Closed ? Fail(3420, DaoErrors.ObjectInvalid, publishesErrorInfo: true) : AssignValue(Recordset, Column, value);

    /// <summary>
    /// A Value put (II-10 §21 F-91; W-05/edit-frames): the value, read through one by-reference level, converts into the
    /// frame's temporary candidate and is accepted, or fails from DAO.Field with an IErrorInfo (3020 without an edit, 3027
    /// on a snapshot, 3421 a conversion, 3316 under ValidateOnSet; assign-without-edit-by-type, stage-validate-on-set).
    /// </summary>
    public static CallResult AssignValue(RecordsetObject recordset, ColumnDescriptor column, nint value)
    {
        if (recordset.AfterRollback != RecordsetAfterRollback.Valid)
        {
            return FieldsObject.AfterRollbackNamed("Field.Value put");
        }
        if (recordset.Frame(out CallResult refused) is not { } frame)
        {
            // dbReadOnly, from DAO.Field with an IErrorInfo (r-cursor-1b/option-readonly-write): 3027 on a dynaset, 3020 on table-type.
            return !recordset.ReadOnlyByOption ? refused
                : recordset.RecordsetType == 2 ? FieldFailure(MutationError.ReadOnly, "Cannot update. Database or object is read-only.")
                : FieldFailure(MutationError.NoEdit, "Update or CancelUpdate without AddNew or Edit.");
        }
        NativeVariant incoming = ByRefValues.Dereference(Args.Cell(value));
        return EditPublication.Of(frame.Assign(column, incoming));
    }

    /// <summary>ValidateOnSet (EXPR-STAGE): True runs the field's rule at the put, False at Update.</summary>
    private CallResult SetValidateOnSet(nint value)
    {
        if (Recordset.Frame(out CallResult refused) is not { } frame)
        {
            return Recordset.ReadOnlyByOption
                ? CallResult.Fail(Failure.NotModelled("NotModelled.Edit.ReadOnlyValidateOnSet", "ValidateOnSet put on a dbReadOnly recordset (not recorded)"))
                : refused;
        }
        frame.SetValidateOnSet(Column, Args.Bool(value, omitted: false) ?? false);
        return CallResult.Nothing;
    }

    /// <summary>The published result of a field read that is not borrowed text.</summary>
    private static CallResult Published(FieldResult read, ColumnDescriptor column)
    {
        if (read.Failure == CursorFailure.NoCurrentRecord)
        {
            return FieldFailure(3021, DaoErrors.NoCurrentRecord);
        }
        if (read.Failure == CursorFailure.DeletedRecord)
        {
            return FieldFailure(3167, DaoErrors.RecordDeleted);
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
    private static CallResult? DecimalRefusal(NativeDecimal value, ColumnDescriptor column)
    {
        if (column.CalculatedResultType is null && column.NumericPrecision > 28)
        {
            return column.NumericPrecision == 38
                ? FieldFailure(3000, "Reserved error (-1038); there is no message for this error.")
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

    protected override void OnFinalRelease()
    {
        _properties?.Release();
        _properties = null;
        _fields.Released(_ordinal);
    }

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

/// <summary>
/// How a recordset stands after a Rollback of its Workspace (qualified.md R-TXN-1 TXN-CLOSE, the recordset after the
/// resolution). These are ACE's states as the records show them, not a closed recordset: Close still succeeds.
/// </summary>
internal enum RecordsetAfterRollback : byte
{
    /// <summary>No Rollback changed it (CommitTrans never does).</summary>
    Valid,

    /// <summary>
    /// <b>Opened before the level, its current record is gone.</b> A dynaset opened outside every level, after the
    /// outermost Rollback: Field.Value and Move 0 give 3021 until MoveFirst repositions it.
    /// </summary>
    LostCurrentRecord,

    /// <summary>
    /// <b>Opened inside the level, it is invalid but not closed.</b> Field.Value, Move 0, MoveFirst and Bookmark give 3420,
    /// yet EditMode answers 0, LastModified a null array and Close succeeds.
    /// </summary>
    Invalid,

    /// <summary>A case the records do not show (a snapshot or forward-only recordset, an inner level): every member but Close is named.</summary>
    NotRecorded,
}

/// <summary>What LastModified answers.</summary>
internal enum LastModifiedMark : byte
{
    /// <summary>No Update yet: not recorded.</summary>
    None,

    /// <summary>The bookmark of the row an Edit Update changed.</summary>
    Row,

    /// <summary>An AddNew Update: the new row's bookmark is not identified.</summary>
    NewRow,

    /// <summary>After the outermost Rollback: a null array.</summary>
    Cleared,
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

    /// <summary>dbReadOnly is set and dbForwardOnly is not: the one cause of a not-updatable writable open whose edit errors are recorded.</summary>
    public static bool ReadOnlyOnly(int options) => (options & (ReadOnly | ForwardOnly)) == ReadOnly;

    public static bool Bookmarkable(int type, int options) => type switch
    {
        1 => true,
        8 => false,
        _ => (options & ForwardOnly) == 0,
    };
}