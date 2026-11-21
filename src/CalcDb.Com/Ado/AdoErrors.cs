using CalcDb.Com.Com;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Ado;

/// <summary>
/// One immutable provider error entry in a Connection's Errors collection (II-12 §28; R-ADO-1). Held Error objects keep
/// their snapshot values after Errors.Clear() or subsequent errors (r-q-cursor-dao/errado-held-after-clear).
/// </summary>
internal readonly record struct AdoErrorEntry(
    int Number,
    string Source,
    string Description,
    string HelpFile,
    int HelpContext,
    string SqlState,
    int NativeError);

/// <summary>
/// ADODB.Error (Error, dual): read-only snapshot of one provider error entry.
/// </summary>
internal sealed class AdoErrorObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Error;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Number", (self, args) => Int32(((AdoErrorObject)self)._entry.Number))
        .Get("Source", (self, args) => Text(((AdoErrorObject)self)._entry.Source))
        .Get("Description", (self, args) => Text(((AdoErrorObject)self)._entry.Description))
        .Get("HelpFile", (self, args) => OptionalText(((AdoErrorObject)self)._entry.HelpFile))
        .Get("HelpContext", (self, args) => Int32(((AdoErrorObject)self)._entry.HelpContext))
        .Get("SQLState", (self, args) => OptionalText(((AdoErrorObject)self)._entry.SqlState))
        .Get("NativeError", (self, args) => Int32(((AdoErrorObject)self)._entry.NativeError));

    private static CallResult OptionalText(string value) => value.Length == 0 ? CallResult.OfOwnedBstr(0) : Text(value);

    private readonly AdoErrorEntry _entry;

    public AdoErrorObject(AdoErrorEntry entry) : base(Decl, Vtable, "ADODB.Error")
    {
        _entry = entry;
    }

    protected override OperationTable Table => Operations;
}

/// <summary>
/// A Connection's live Errors collection (II-12 §28; r-q-cursor-dao/errado-held-after-clear, errbase-two-ado-connections,
/// errado-clear-by-operation). One instance per Connection, isolated across connections; provider failures replace the
/// entries, successful Open/Close and Clear empty them, and ADO's own client errors (0x800A*) leave them untouched.
/// </summary>
internal sealed unsafe class AdoErrorsObject : AdoObject
{
    private static readonly InterfaceDecl Decl = AdoTypes.Errors;
    private static readonly nint Vtable = DualEntries.Build(Decl);
    private static readonly OperationTable Operations = new OperationTable(Decl)
        .Get("Count", (self, args) => Int32(((AdoErrorsObject)self)._entries.Length))
        .Get("Item", (self, args) => ((AdoErrorsObject)self).Item(args[0]))
        .Method("Clear", (self, args) => ((AdoErrorsObject)self).ClearMethod())
        .Method("Refresh", (self, args) => CallResult.Nothing);

    private AdoErrorEntry[] _entries = [];
    private AdoErrorObject?[] _wrappers = [];

    public AdoErrorsObject() : base(Decl, Vtable, "ADODB.Errors")
    {
    }

    protected override OperationTable Table => Operations;

    public void Clear() => Replace([]);

    public void Replace(params AdoErrorEntry[] entries)
    {
        ReleaseWrappers();
        _entries = entries;
        _wrappers = entries.Length == 0 ? [] : new AdoErrorObject?[entries.Length];
    }

    private CallResult ClearMethod()
    {
        Clear();
        return CallResult.Nothing;
    }

    private CallResult Item(nint index)
    {
        if (index == 0)
        {
            return NotImplemented("Errors.Item with Missing index (not asked)");
        }
        NativeVariant* v = Args.Cell(index);
        switch ((VarType)v->Vt)
        {
            case VarType.I2:
            case VarType.I4:
                long i = (VarType)v->Vt == VarType.I2 ? (short)v->Int64 : (int)v->Int64;
                if (i < 0 || i >= _entries.Length)
                {
                    return Fail(AdoErrors.ItemNotFound);
                }
                int ordinal = (int)i;
                return Object(_wrappers[ordinal] ??= new AdoErrorObject(_entries[ordinal]));
            case VarType.Bstr:
                return Fail(AdoErrors.ItemNotFound);
            default:
                return NotImplemented("Errors.Item with this index form (not asked)");
        }
    }

    private void ReleaseWrappers()
    {
        foreach (AdoErrorObject? wrapper in _wrappers)
        {
            wrapper?.Release();
        }
    }

    protected override void OnFinalRelease() => ReleaseWrappers();
}
