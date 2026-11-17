using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Dao;

/// <summary>The operations a DAO class implements, by declared member (one table per class).</summary>
internal sealed class OperationTable(InterfaceDecl declaration)
{
    private readonly Dictionary<MemberDecl, Operation> _operations = new(ReferenceEqualityComparer.Instance);

    public OperationTable Get(string name, Operation op) => Add(name, InvokeKind.PropertyGet, op);

    public OperationTable Put(string name, Operation op) => Add(name, InvokeKind.PropertyPut, op);

    public OperationTable Method(string name, Operation op) => Add(name, InvokeKind.Method, op);

    private OperationTable Add(string name, InvokeKind kind, Operation op)
    {
        _operations[declaration.Member(name, kind)] = op;
        return this;
    }

    public Operation? Find(MemberDecl member) => _operations.GetValueOrDefault(member);
}

/// <summary>The DAO errors the read path raises, with ACE's exact texts (records named at each use).</summary>
internal static class DaoErrors
{
    /// <summary>3021 (r-str-ado-act/str-after-eof-close).</summary>
    public const string NoCurrentRecord = "No current record.";

    /// <summary>3420 (R-ACT-1 ACT-RETAIN).</summary>
    public const string ObjectInvalid = "Object invalid or no longer set.";

    /// <summary>3265 (R-DAO-1 DAO-COLL).</summary>
    public const string ItemNotFound = "Item not found in this collection.";

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
internal abstract unsafe class DaoObject(InterfaceDecl declaration, nint vtable, EngineState engine, string source) : DispatchObject(declaration, vtable)
{
    public EngineState Engine { get; } = engine;

    public string Source { get; } = source;

    protected abstract OperationTable Table { get; }

    protected override Operation? Find(MemberDecl member) => Table.Find(member);

    protected override void OnFailure(Failure failure)
    {
        if (failure.Kind == FailureKind.Dao)
        {
            Engine.Record(failure);
        }
    }

    public CallResult Fail(int number, string description, bool publishesErrorInfo = false) =>
        CallResult.Fail(Failure.Dao(number, Source, description) with { PublishesErrorInfo = publishesErrorInfo });

    /// <summary>A returned object: a new reference to its IDispatch head, owned by the result.</summary>
    public static CallResult Object(ComObject target)
    {
        nint head;
        target.QueryInterface(ComIids.IDispatch, &head);
        return CallResult.Of(OwnedVariant.TakeObject(head, VarType.Dispatch));
    }

    public static CallResult Text(string text) => CallResult.Of(OwnedVariant.FromText(text));

    public static CallResult Int16(int value) => CallResult.Of(OwnedVariant.FromInt16((short)value));

    public static CallResult Int32(int value) => CallResult.Of(OwnedVariant.FromInt32(value));

    public static CallResult Bool(bool value) => CallResult.Of(OwnedVariant.FromBoolean(value));
}

/// <summary>Collection lookup by a VARIANT index (R-DAO-1 DAO-COLL).</summary>
internal static unsafe class CollectionLookup
{
    /// <summary>
    /// The ordinal an index names: names compare case-insensitively with composed and decomposed accents equal; I2, I4,
    /// UI1, R8 (rounded to nearest even), BOOL 0 and DATE are ordinals; digit strings, BOOL -1, Null, Empty and Missing
    /// name nothing. An object index is not handled here (ACE gives 3001 and 3421).
    /// </summary>
    public static int? Find(nint index, int count, Func<int, string> nameAt, out bool objectIndex)
    {
        objectIndex = false;
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
            string? name = Args.Text(index);
            if (name is null)
            {
                return null;
            }
            string wanted = name.Normalize(System.Text.NormalizationForm.FormC);
            for (int i = 0; i < count; i++)
            {
                if (string.Equals(nameAt(i).Normalize(System.Text.NormalizationForm.FormC), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            return null;
        }
        if (vt == VarType.Bool)
        {
            return (short)Value(v) == 0 && count > 0 ? 0 : null;
        }
        if (vt is VarType.Null or VarType.Empty or VarType.Error)
        {
            return null;
        }
        using OwnedVariant? number = OwnedVariant.ChangeType(v, VarType.R8, out _);
        if (number is null)
        {
            return null;
        }
        double d = Math.Round(number.Pointer->Double, MidpointRounding.ToEven);
        return d >= 0 && d < count ? (int)d : null;
    }

    private static long Value(NativeVariant* v) => ((VarType)v->Vt & VarType.ByRef) != 0 ? *(short*)v->Pointer : v->Int64;
}
