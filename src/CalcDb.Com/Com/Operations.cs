using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Com;

/// <summary>The operations a DAO or ADO class implements, by declared member (one table per class).</summary>
internal sealed class OperationTable(InterfaceDecl declaration)
{
    private readonly Dictionary<MemberDecl, Operation> _operations = new(ReferenceEqualityComparer.Instance);

    public OperationTable Get(string name, Operation op) => Add(name, InvokeKind.PropertyGet, op);

    public OperationTable Put(string name, Operation op) => Add(name, InvokeKind.PropertyPut, op);

    public OperationTable PutRef(string name, Operation op) => Add(name, InvokeKind.PropertyPutRef, op);

    public OperationTable Method(string name, Operation op) => Add(name, InvokeKind.Method, op);

    private OperationTable Add(string name, InvokeKind kind, Operation op)
    {
        _operations[declaration.Member(name, kind)] = op;
        return this;
    }

    public Operation? Find(MemberDecl member) => _operations.GetValueOrDefault(member);
}

/// <summary>The results operations return, shared by the DAO and ADO objects.</summary>
internal static unsafe class Results
{
    /// <summary>A returned object: a new reference to its IDispatch head, owned by the result.</summary>
    public static CallResult Object(ComObject target)
    {
        nint head;
        target.QueryInterface(ComIids.IDispatch, &head);
        return CallResult.OfOwnedObject(head);
    }

    /// <summary>A null object result (VT_DISPATCH with a null pointer), such as a Command's ActiveConnection before one is set.</summary>
    public static CallResult NullObject { get; } = CallResult.OfScalar(VarType.Dispatch, 0);

    /// <summary>A string result as an inline owned BSTR of its exact units (no OwnedVariant; task hdw6).</summary>
    public static CallResult Text(string text) => CallResult.OfOwnedBstr(Bstrs.FromUnits(text.Length, text, static (dest, s) => s.AsSpan().CopyTo(dest)));

    public static CallResult Int16(int value) => CallResult.OfScalar(VarType.I2, (short)value);

    public static CallResult Int32(int value) => CallResult.OfScalar(VarType.I4, (uint)value);

    /// <summary>VT_BOOL: VARIANT_TRUE is -1 in the low 16 bits.</summary>
    public static CallResult Bool(bool value) => CallResult.OfScalar(VarType.Bool, value ? 0xFFFF : 0);
}
