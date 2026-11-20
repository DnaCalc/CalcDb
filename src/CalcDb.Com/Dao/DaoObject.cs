using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Dao;

/// <summary>The DAO errors the read path raises, with ACE's exact texts (records named at each use).</summary>
internal static class DaoErrors
{
    /// <summary>3021 (r-str-ado-act/str-after-eof-close).</summary>
    public const string NoCurrentRecord = "No current record.";

    /// <summary>3420 (R-ACT-1 ACT-RETAIN).</summary>
    public const string ObjectInvalid = "Object invalid or no longer set.";

    /// <summary>3219 (r-cursor-1/pos-moves-forwardonly).</summary>
    public const string InvalidOperation = "Invalid operation.";

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

    protected override Operation? Find(MemberDecl member) => Table.Find(member);

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
        if (!OwnedVariant.TryChangeScalar(v, VarType.R8, out NativeVariant number, out _))
        {
            return null;
        }
        double d = Math.Round(number.Double, MidpointRounding.ToEven);
        return d >= 0 && d < count ? (int)d : null;
    }

    private static long Value(NativeVariant* v) => ((VarType)v->Vt & VarType.ByRef) != 0 ? *(short*)v->Pointer : v->Int64;
}