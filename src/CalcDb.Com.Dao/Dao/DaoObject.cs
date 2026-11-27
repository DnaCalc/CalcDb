using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Values;

namespace CalcDb.Com.Dao;

/// <summary>The DAO errors the read path raises, with ACE's exact texts (records named at each use).</summary>
internal static class DaoErrors
{
    /// <summary>3021 (r-str-ado-act/str-after-eof-close).</summary>
    public const string NoCurrentRecord = "No current record.";

    /// <summary>3167 (R-CURSOR-1 CUR-POS, CUR-ID: reading or editing a deleted row).</summary>
    public const string RecordDeleted = "Record is deleted.";

    /// <summary>3420 (R-ACT-1 ACT-RETAIN).</summary>
    public const string ObjectInvalid = "Object invalid or no longer set.";

    /// <summary>3219 (r-cursor-1/pos-moves-forwardonly).</summary>
    public const string InvalidOperation = "Invalid operation.";

    /// <summary>3267 (r-dao-1b/field-properties-enumerated: FieldSize of a TableDef field).</summary>
    public const string RecordsetFieldOnly = "Property can be set only when the Field is part of a Recordset object's Fields collection.";

    /// <summary>3270 (R-DAO-1 DAO-COLL: a missing name in Properties.Item; r-dao-1-err-1/prop-enumerate-and-context).</summary>
    public const string PropertyNotFound = "Property not found.";

    /// <summary>3271 (r-dao-1-err-1/boot-createworkspace: Workspaces.Append of a Workspace named "").</summary>
    public const string InvalidPropertyValue = "Invalid property value.";

    /// <summary>3358 (R-DAO-1 DAO-META: Container and Document Permissions before a put; r-dao-1b/containers-members-each).</summary>
    public const string NoWorkgroupFile = "Cannot open the Microsoft Access database engine workgroup information file.";

    /// <summary>3265 (R-DAO-1 DAO-COLL).</summary>
    public const string ItemNotFound = "Item not found in this collection.";

    /// <summary>3001 (R-ERR-1 ERR-EXCEP: argument-count failures of Database and Recordset members).</summary>
    public const string InvalidArgument = "Invalid argument.";

    /// <summary>3019 (r-cursor-1/seek-arguments: Seek with no Index set).</summary>
    public const string NoCurrentIndex = "Operation invalid without a current index.";

    /// <summary>3250 (r-cursor-1/seek-arguments: more keys than index columns).</summary>
    public const string CouldNotBuildKey = "Could not build key.";

    /// <summary>3251 (r-cursor-1/seek-arguments: Seek on a dynaset).</summary>
    public const string NotSupportedForType = "Operation is not supported for this type of object.";

    /// <summary>3421 (r-cursor-1/seek-arguments: a non-numeric text key for a Long column).</summary>
    public const string ConversionError = "Data type conversion error.";

    /// <summary>3010 (R-CAT-1 CAT-B: a table name the catalog already lists, compared without case).</summary>
    public static string TableExists(string name) => $"Table '{name}' already exists.";

    /// <summary>3012 (R-CAT-1 CAT-Q; r-dao-1-err-1/draft-querydef, r-cat-1/names: a QueryDef name the catalog already lists).</summary>
    public static string ObjectExists(string name) => $"Object '{name}' already exists.";

    /// <summary>3125 (R-CAT-1 CAT-B; r-cat-1/names, r-dao-1-err-1/err-invalid-names: an invalid object name).</summary>
    public static string InvalidName(string name) => $"'{name}' is not a valid name. Make sure that it does not include invalid characters or punctuation and that it is not too long.";

    /// <summary>3264 (r-dao-1-err-1/draft-tabledef-field: TableDefs.Append of a TableDef with no fields).</summary>
    public const string NoFieldDefined = "No field defined--cannot append TableDef or Index.";

    /// <summary>3259 (r-dao-1-err-1/draft-tabledef-field: CreateField with Type 9999).</summary>
    public const string InvalidFieldType = "Invalid field data type.";

    /// <summary>3367 (r-dao-1-err-1/draft-tabledef-field: Fields.Append of a field the collection already holds).</summary>
    public const string AlreadyInCollection = "Cannot append. An object with that name already exists in the collection.";

    /// <summary>3162 (r-sort-3b/mix-seek-b, r-cursor-1-seek-keys/yes-no-null-key: a Null or Empty key for a Yes/No index column).</summary>
    public const string NullToNonVariant = "You tried to assign the Null value to a variable that is not a Variant data type.";

    /// <summary>3032 (r-cat-1b/tabledef-members-ddl: ConflictTable, ReplicaFilter of a TableDef).</summary>
    public const string CannotPerform = "Cannot perform this operation.";

    /// <summary>3110 (r-cat-1b/tabledef-members-ddl: the Indexes of a system table for the default Admin).</summary>
    public static string NoReadDefinitions(string name) => $"Could not read definitions; no read definitions permission for table or query '{name}'.";

    /// <summary>3800 (r-cursor-1/id-index-switch).</summary>
    public static string NotAnIndex(string name) => $"'{name}' is not an index in this table.";

    /// <summary>3011 (r-oracle-1/failed-call-t1).</summary>
    public static string ObjectNotFound(string name) =>
        $"The Microsoft Access database engine could not find the object '{name}'. Make sure the object exists and that you spell its name and the path name correctly. If '{name}' is not a local object, check your network connection or contact the server administrator.";

    /// <summary>3024 (r-err-1b/missing-file).</summary>
    public static string FileNotFound(string path) => $"Could not find file '{path}'.";

    /// <summary>3044 (r-q-wire/thread-engine-identity-main-then-mta).</summary>
    public static string NotAValidPath(string path) =>
        $"'{path}' is not a valid path. Make sure that the path name is spelled correctly and that you are connected to the server on which the file resides.";

    /// <summary>3204 (r-cat-1/create-accdb-existing-destination).</summary>
    public const string DatabaseAlreadyExists = "Database already exists.";

    /// <summary>
    /// 3045 (R-LOCK-1 EX-PUBLIC: evidence/r-lock-1-2/*-inproc-*-excl-ro-then-*-rw, mdb-inproc-*-shared-ro-then-excl-rw;
    /// evidence/r-q46-dao-ddl-multi/mh-excl-* calls 52-61): the path as OpenDatabase was given it.
    /// </summary>
    public static string FileAlreadyInUse(string path) => $"Could not use '{path}'; file already in use.";

    /// <summary>
    /// 3704 (R-LOCK-2 RO-INPROC: evidence/r-lock-1-2/accdb-inproc-ws1-shared-ro-then-excl-rw and
    /// accdb-inproc-ws2-shared-ro-then-excl-rw; evidence/r-q46-dao-ddl-multi/mh-excl-samews-accdb call 79): the first
    /// binding's user and this computer's name.
    /// </summary>
    public static string AlreadyOpenedByUser(string user, string machine) =>
        $"You attempted to open a database that is already opened by user '{user}' on machine '{machine}'. Try again when the database is available.";

    /// <summary>3246 (r-cat-1/create-accdb-in-transaction).</summary>
    public const string NotSupportedInTransactions = "Operation not supported in transactions.";

    /// <summary>3366 (r-q46-dao-ddl-multi/rel-errors-*, r-dao-1-err-1/draft-index-relation: Relations.Append with 0 fields).</summary>
    public const string RelationNoFields = "Cannot append a relation with no fields defined.";

    /// <summary>3368 (r-q47-bigint-expr-edges/rel-mismatch-3368-*: enforced Relations.Append with mismatched field types).</summary>
    public const string RelationTypeMismatch = "Relationship must be on the same number of fields with the same data types.";

    /// <summary>3201 (r-q46-dao-ddl-multi/rel-errors-*, r-cat-1b/rel-append-over-existing-orphans: orphan child row).</summary>
    public static string RelatedRecordRequired(string parentTable) => $"You cannot add or change a record because a related record is required in table '{parentTable}'.";

    /// <summary>3170 (r-sort-3/create-locale-spellings: locale without a leading semicolon).</summary>
    public const string CouldNotFindInstallableIsam = "Could not find installable ISAM.";

    /// <summary>3228 (r-sort-3/create-locale-spellings: LANGID=0x0000).</summary>
    public const string CollatingSequenceNotSupported = "Selected collating sequence not supported by the operating system.";

    /// <summary>3000 (r-wire/read-dao, r-wire/read-sql, r-dec-read/*-daofield-*: non-Null read of a precision-38 Decimal column).</summary>
    public const string DecimalPrecision38 = "Reserved error (-1038); there is no message for this error.";

    /// <summary>3035 (r-wire/getrows-numrows-args: GetRows(Int32.MaxValue)).</summary>
    public const string SystemResourceExceeded = "System resource exceeded.";

    /// <summary>3008 (r-q50-jk44-ddl-vis/dao-opt-deny-append-*: open table with dbDenyWrite / dbDenyRead).</summary>
    public static string TableAlreadyOpenedExclusively(string table) =>
        $"The table '{table}' is already opened exclusively by another user, or it is already open through the user interface and cannot be manipulated programmatically.";

    /// <summary>3164 (r-q50-jk44-ddl-vis/dao-opt-join-seechanges-*: join key field put under dbConsistent).</summary>
    public const string FieldCannotBeUpdated = "Field cannot be updated.";

    /// <summary>3022 (r-q52-ddl-make-domain/dao-idx-errors-*, r-q54-excl-ddl-ix-cmd/ix-unique-dup-3022-bytes-*).</summary>
    public const string DuplicateKey = "The changes you requested to the table were not successful because they would create duplicate values in the index, primary key, or relationship. Change the data in the field or fields that contain duplicate data, remove the index, or redefine the index to permit duplicate entries and try again.";

    /// <summary>3027 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append / Indexes.Delete on read-only Database).</summary>
    public const string ReadOnly = "Cannot update. Database or object is read-only.";

    /// <summary>3058 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append Required/Primary over Null row).</summary>
    public const string NullInIndex = "Index or primary key cannot contain a Null value.";

    /// <summary>3268 (r-q52-ddl-make-domain/dao-idx-errors-*: Primary/Unique/Required/IgnoreNulls put on appended Index).</summary>
    public const string CannotSetOnceInCollection = "Cannot set this property once the object is part of a collection.";

    /// <summary>3280 (r-q52-ddl-make-domain/dao-idx-errors-*: TableDef.Fields.Delete of an indexed field).</summary>
    public const string CannotDeleteIndexedField = "Cannot delete a field that is part of an index or is needed by the system.";

    /// <summary>3281 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Delete of a PK or FK index used in a relationship).</summary>
    public const string CannotDeleteIndexInUse = "Cannot delete this index or table. It is either the current index or is used in a relationship.";

    /// <summary>3283 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append of a second Primary key).</summary>
    public const string PrimaryKeyExists = "Primary key already exists.";

    /// <summary>3284 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append with a duplicate index name).</summary>
    public const string IndexExists = "Index already exists.";

    /// <summary>3285 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append with duplicate column in index).</summary>
    public const string InvalidIndexDefinition = "Invalid index definition.";

    /// <summary>3409 (r-q52-ddl-make-domain/dao-idx-errors-*: Indexes.Append naming a missing column).</summary>
    public static string InvalidIndexField(string fieldName) => $"Invalid field definition '{fieldName}' in definition of index or relationship.";

    /// <summary>3384 (r-q51-codecs-props-compact/prop-errors-*: Properties.Delete on a built-in property).</summary>
    public const string CannotDeleteBuiltInProperty = "Cannot delete a built-in property.";

    /// <summary>3385 (r-q51-codecs-props-compact/prop-errors-*: CreateProperty / Value put with Null or empty string).</summary>
    public const string UserPropertyNoNull = "User-defined properties do not support a Null value.";

    /// <summary>3386 (r-q51-codecs-props-compact/prop-errors-*: Properties.Append of a draft Property before Value is set).</summary>
    public const string PropertyValueRequired = "Property 'Value' must be set before using this method.";

    /// <summary>3273 (r-dao-1b/coll-append-delete-unsupported: Workspaces.Delete, Recordset.Fields.Append/Delete, QueryDef.Fields.Delete).</summary>
    public const string MethodNotApplicable = "Method not applicable for this object.";

    /// <summary>3303 (r-dao-1b/coll-delete-field-indexed: TableDef.Fields.Delete of a field participating in a relationship).</summary>
    public const string CannotDeleteFieldInRelation = "Cannot delete this field. It is part of one or more relationships.";

    /// <summary>3151 (r-cat-1b/qd-passthrough-saved-run: OpenRecordset on a saved PassThrough QueryDef with an unavailable ODBC DSN).</summary>
    public static string OdbcConnectionFailed(string dsn) => $"ODBC--connection to '{dsn}' failed.";
}

/// <summary>
/// A DAO wrapper: its class's operation table, the engine state whose Errors collection records its failures
/// (R-ORACLE-1 OR-OBS: the latest failure replaces the collection; a success never clears it) and its error source,
/// "DAO.&lt;class&gt;" as ACE names it.
/// </summary>
internal abstract unsafe class DaoObject : DispatchObject
{
    protected DaoObject(InterfaceDecl declaration, nint vtable, EngineState engine, string source) : base(declaration, vtable)
    {
        Engine = engine;
        Source = source;
        CostCounters.Current.WrappersCreated++;
    }

    public EngineState Engine { get; }

    public string Source { get; }

    protected abstract OperationTable Table { get; }

    /// <summary>True when every member answers 3420 (a closed Database or Recordset, or a child of one: R-ACT-1 ACT-RETAIN).</summary>
    public virtual bool Invalid => false;

    protected override Operation? Find(MemberDecl member) => Table.Find(member);

    /// <summary>
    /// DAO wrong-InvokeKind dispatch rules (W-08, R-DAO-1, R-ERR-1):
    /// <list type="bullet">
    /// <item>PropertyPut on a read-only PropertyGet -> DAO 3001 (<c>Invalid argument.</c>) with IErrorInfo
    /// (r-dao-1-err-1/prop-enumerate-and-context call 29 <c>db.Version</c>; r-dao-1-err-1/err-bind-arity-member
    /// calls 17-18 <c>db.Name</c>, <c>eng.Version</c>).</item>
    /// <item>PropertyGet on a write-only PropertyPut (<c>eng.DefaultUser</c>, <c>eng.DefaultPassword</c>:
    /// r-dao-1b/eng-members-each calls 6-7, r-dao-1-err-1/prop-enumerate-and-context call 25), PropertyGet on a
    /// Method (<c>_NewEnum</c>: r-dao-1b/coll-newenum-get calls 24-38), Method on a PropertyGet (<c>eng.Version()</c>:
    /// r-dao-1-err-1/err-bind-arity-member call 19), or PropertyPutRef on a non-PutRef property (<c>db.Connect</c>:
    /// r-dao-1-err-1/err-bind-arity-member call 21) -> DAO 3251 (<c>Operation is not supported for this type of object.</c>)
    /// with IErrorInfo.</item>
    /// </list>
    /// Unknown DISPIDs and unrecorded flag combinations return null (<c>DISP_E_MEMBERNOTFOUND</c>).
    /// </summary>
    protected override Failure? WrongInvokeKindFailure(int dispId, ushort flags)
    {
        bool hasMethod = false, hasGet = false, hasPut = false, hasPutRef = false;
        IReadOnlyList<MemberDecl> members = Declaration.Members;
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i].MemberId != dispId)
            {
                continue;
            }
            switch (members[i].Kind)
            {
                case InvokeKind.Method: hasMethod = true; break;
                case InvokeKind.PropertyGet: hasGet = true; break;
                case InvokeKind.PropertyPut: hasPut = true; break;
                case InvokeKind.PropertyPutRef: hasPutRef = true; break;
            }
        }
        if (!(hasMethod || hasGet || hasPut || hasPutRef))
        {
            return null;
        }
        if (flags == (ushort)InvokeKind.PropertyPut && hasGet && !hasPut)
        {
            return Failure.Dao(3001, Source, DaoErrors.InvalidArgument) with { PublishesErrorInfo = true };
        }
        if ((flags == (ushort)InvokeKind.PropertyGet && (hasPut || hasMethod))
            || (flags == (ushort)InvokeKind.Method && hasGet)
            || (flags == (ushort)InvokeKind.PropertyPutRef && (hasGet || hasPut) && !hasPutRef))
        {
            return Failure.Dao(3251, Source, DaoErrors.NotSupportedForType) with { PublishesErrorInfo = true };
        }
        return null;
    }

    protected static CallResult NewEnumResult(IDaoEnumerableCollection collection)
    {
        var enumerator = new DaoEnumVariant(collection);
        nint unknown;
        enumerator.QueryInterface(ComIids.IUnknown, &unknown);
        enumerator.Release();
        return CallResult.Of(OwnedVariant.TakeObject(unknown, VarType.Unknown));
    }

    protected override void OnFailure(Failure failure)
    {
        if (failure.Kind == FailureKind.Dao)
        {
            Engine.Record(failure);
        }
    }

    /// <summary>DAO 3001 with an IErrorInfo, as ACE reports an argument-count failure of a Database or Recordset member (R-ERR-1 ERR-EXCEP).</summary>
    protected Failure InvalidArgumentCount() => Failure.Dao(3001, Source, DaoErrors.InvalidArgument) with { PublishesErrorInfo = true };

    public CallResult Fail(int number, string description, bool publishesErrorInfo = false) =>
        CallResult.Fail(Failure.Dao(number, Source, description) with { PublishesErrorInfo = publishesErrorInfo });

    public static CallResult Object(ComObject target) => Results.Object(target);

    public static CallResult Text(string text) => Results.Text(text);

    public static CallResult Int16(int value) => Results.Int16(value);

    public static CallResult Int32(int value) => Results.Int32(value);

    public static CallResult Bool(bool value) => Results.Bool(value);
}

/// <summary>
/// The item names of a collection, prepared once for lookup (R-DAO-1 DAO-COLL, task fbh8): composed (FormC) and
/// compared case-insensitively, the first item of a name winning. A request in ASCII, already composed, is looked up
/// by its borrowed characters; any other is composed first.
/// </summary>
internal sealed class CollectionNames
{
    private readonly Dictionary<string, int> _first;
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _byChars;

    public CollectionNames(IReadOnlyList<string> names)
    {
        Count = names.Count;
        _first = new Dictionary<string, int>(names.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            _first.TryAdd(names[i].Normalize(System.Text.NormalizationForm.FormC), i);
        }
        _byChars = _first.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int Count { get; }

    public int? Find(ReadOnlySpan<char> name)
    {
        if (System.Text.Ascii.IsValid(name))
        {
            return _byChars.TryGetValue(name, out int i) ? i : null;
        }
        return _first.TryGetValue(new string(name).Normalize(System.Text.NormalizationForm.FormC), out int j) ? j : null;
    }
}

/// <summary>Collection lookup by a VARIANT index (R-DAO-1 DAO-COLL).</summary>
internal static unsafe class CollectionLookup
{
    /// <summary>
    /// The ordinal an index names: names compare case-insensitively with composed and decomposed accents equal; I2, I4,
    /// UI1, R8 (rounded to nearest even), BOOL 0 and DATE are ordinals; digit strings, BOOL -1, Null, Empty and Missing
    /// name nothing. An object index is not handled here (ACE gives 3001 and 3421).
    /// </summary>
    public static int? Find(nint index, CollectionNames names, out bool objectIndex)
    {
        int count = names.Count;
        objectIndex = false;
        CostCounters counters = CostCounters.Current;
        counters.CollectionLookups++;
        if (index == 0)
        {
            return null;
        }
        NativeVariant* v = Args.Cell(index);
        VarType vt = (VarType)v->Vt & ~VarType.ByRef;
        if ((vt & VarType.TypeMask) is VarType.Dispatch or VarType.Unknown)
        {
            objectIndex = true;
            return null;
        }
        if (vt == VarType.Bstr)
        {
            counters.CollectionNameProbes++;
            if (Args.TypeOf(index) == VarType.Bstr)
            {
                // The BSTR's characters, borrowed for the call (embedded NUL kept); a null BSTR names nothing.
                nint bstr = v->Pointer;
                return bstr == 0 ? null : names.Find(Bstrs.Chars(bstr));
            }
            string? name = Args.Text(index);
            return name is null ? null : names.Find(name);
        }
        if (vt == VarType.Bool)
        {
            return (short)Value(v) == 0 && count > 0 ? 0 : null;
        }
        if (vt is VarType.Null or VarType.Empty or VarType.Error)
        {
            return null;
        }
        if (!OwnedVariant.TryChangeScalar(v, VarType.R8, SessionLocale.Lcid, out NativeVariant number, out _))
        {
            return null;
        }
        double d = Math.Round(number.Double, MidpointRounding.ToEven);
        return d >= 0 && d < count ? (int)d : null;
    }

    private static long Value(NativeVariant* v) => ((VarType)v->Vt & VarType.ByRef) != 0 ? *(short*)v->Pointer : v->Int64;
}

/// <summary>A DAO collection enumerable through <see cref="DaoEnumVariant"/> (<c>_NewEnum</c>).</summary>
internal interface IDaoEnumerableCollection
{
    EngineState Engine { get; }
    bool IsEnumInvalid { get; }
    string EnumClosedSource { get; }
    int EnumCount { get; }
    nint EnumDispatchAt(int index);
    void AddRefCollection();
    void ReleaseCollection();
}

/// <summary>
/// COM <c>IEnumVARIANT</c> (<c>{00020404-0000-0000-C000-000000000046}</c>) over a DAO collection
/// (r-q2-wire/obs-errors-foreach-enumeration, r-q2-wire/retain-enum-dao-after-close):
/// <c>Next</c> returns <c>S_OK</c> with <c>VT_DISPATCH</c> items, <c>S_FALSE</c> at/past end, or <c>0x800A0D5C</c>
/// (DAO 3420 recorded in <c>DBEngine.Errors</c> with the owner's closed source) when the collection is invalid;
/// <c>Skip</c> returns <c>E_FAIL</c> (<c>0x80004005</c>); <c>Reset</c> and <c>Clone</c> return <c>S_OK</c>.
/// </summary>
internal sealed unsafe class DaoEnumVariant : ComObject
{
    public static readonly Guid IidEnumVariant = new("00020404-0000-0000-C000-000000000046");
    private const int DaoObjectInvalidHResult = unchecked((int)0x800A0D5C);
    private const int EFailHResult = unchecked((int)0x80004005);

    private static readonly nint EnumVtable = BuildVtable();

    private readonly IDaoEnumerableCollection _collection;
    private int _index;

    public DaoEnumVariant(IDaoEnumerableCollection collection, int index = 0) : base(EnumVtable)
    {
        _collection = collection;
        _index = index;
        collection.AddRefCollection();
    }

    protected override int HeadFor(in Guid iid) => iid == IidEnumVariant ? 0 : -1;

    private int Next(uint celt, NativeVariant* rgVar, uint* pceltFetched)
    {
        if (rgVar is null)
        {
            return HResults.E_POINTER;
        }
        if (pceltFetched is null && celt > 1)
        {
            return HResults.E_INVALIDARG;
        }
        if (_collection.IsEnumInvalid)
        {
            if (pceltFetched is not null)
            {
                *pceltFetched = 0;
            }
            _collection.Engine.Record(Failure.Dao(3420, _collection.EnumClosedSource, DaoErrors.ObjectInvalid));
            return DaoObjectInvalidHResult;
        }
        uint fetched = 0;
        int count = _collection.EnumCount;
        while (fetched < celt && _index < count)
        {
            nint dispatch = _collection.EnumDispatchAt(_index++);
            OleAut.VariantInit(rgVar + fetched);
            rgVar[fetched].Vt = (ushort)VarType.Dispatch;
            rgVar[fetched].Pointer = dispatch;
            fetched++;
        }
        if (pceltFetched is not null)
        {
            *pceltFetched = fetched;
        }
        return fetched == celt ? HResults.S_OK : HResults.S_FALSE;
    }

    /// <summary>ACE DAO enumerators always return E_FAIL (0x80004005) on Skip (r-q2-wire/obs-errors-foreach-enumeration, retain-enum-dao-after-close).</summary>
    private static int Skip(uint celt)
    {
        _ = celt;
        return EFailHResult;
    }

    private int Reset()
    {
        _index = 0;
        return HResults.S_OK;
    }

    private int Clone(nint* ppenum)
    {
        if (ppenum is null)
        {
            return HResults.E_POINTER;
        }
        var copy = new DaoEnumVariant(_collection, _index);
        int hr = copy.QueryInterface(IidEnumVariant, ppenum);
        copy.Release();
        return hr;
    }

    protected override void OnFinalRelease() => _collection.ReleaseCollection();

    private static nint BuildVtable()
    {
        nint* table = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterfaceEntry;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRefEntry;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&ReleaseEntry;
        table[3] = (nint)(delegate* unmanaged<nint, uint, NativeVariant*, uint*, int>)&NextEntry;
        table[4] = (nint)(delegate* unmanaged<nint, uint, int>)&SkipEntry;
        table[5] = (nint)(delegate* unmanaged<nint, int>)&ResetEntry;
        table[6] = (nint)(delegate* unmanaged<nint, nint*, int>)&CloneEntry;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterfaceEntry(nint self, Guid* iid, nint* result) =>
        result is null || iid is null ? HResults.E_POINTER : Recover<DaoEnumVariant>(self).QueryInterface(*iid, result);

    [UnmanagedCallersOnly]
    private static uint AddRefEntry(nint self) => Recover<DaoEnumVariant>(self).AddRef();

    [UnmanagedCallersOnly]
    private static uint ReleaseEntry(nint self) => Recover<DaoEnumVariant>(self).Release();

    [UnmanagedCallersOnly]
    private static int NextEntry(nint self, uint celt, NativeVariant* rgVar, uint* pceltFetched) =>
        Recover<DaoEnumVariant>(self).Next(celt, rgVar, pceltFetched);

    [UnmanagedCallersOnly]
    private static int SkipEntry(nint self, uint celt) => Skip(celt);

    [UnmanagedCallersOnly]
    private static int ResetEntry(nint self) => Recover<DaoEnumVariant>(self).Reset();

    [UnmanagedCallersOnly]
    private static int CloneEntry(nint self, nint* ppenum) => Recover<DaoEnumVariant>(self).Clone(ppenum);
}