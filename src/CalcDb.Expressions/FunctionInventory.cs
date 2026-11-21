using CalcDb.Values.Scalar;

namespace CalcDb.Expressions;

/// <summary>Whether a function name is available in the standalone expression service (II-10 §13, EXPR-INVENTORY).</summary>
public enum FunctionAvailability : byte
{
    /// <summary>Bound to a kernel operation.</summary>
    Available,

    /// <summary>Not known to standalone ACE: 3072 "Unknown function name" in a Find criteria.</summary>
    Unknown,

    /// <summary>A domain aggregate: 3072 "Function is not available in expressions" in a Find criteria.</summary>
    Domain,
}

/// <summary>
/// The functions an expression may call (II-10 §13: an inventory of executable descriptors). Available: the kernel's
/// functions (qualified.md "R-EXPR-1 Branch B, EXPR-RUNTIME" and the Find criteria note: UCase, Len, Chr, Val, Int, CInt,
/// CDate, DateSerial, IsNull, Left asked in Find). Unknown: the names the standalone service lacks (qualified.md
/// "function availability": Nz, CVErr, … ; Nz and CVErr asked in Find). IsError is available (qualified.md R-EXPR-1
/// Branch B, corrected: r-q3-sql/iserror-forms, r-expr-1/a-null-empty-error-literals). Domain: the domain aggregates (DMax asked
/// in Find). Any other name is not in this inventory: the binder names it instead of guessing whether ACE knows it.
/// </summary>
public static class FunctionInventory
{
    /// <summary>A function's availability, its kernel operation and its result's static type.</summary>
    public readonly record struct Entry(FunctionAvailability Availability, ScalarOperation Operation, StaticType Type);

    private static readonly Dictionary<string, Entry> Names = Build();

    public static Entry? Resolve(string name) => Names.TryGetValue(name, out Entry e) ? e : null;

    private static Dictionary<string, Entry> Build()
    {
        var names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        void Available(string name, ScalarOperation op, StaticType type = StaticType.Value) => names.Add(name, new Entry(FunctionAvailability.Available, op, type));

        Available("CBool", ScalarOperation.CBool, StaticType.Boolean);
        Available("CByte", ScalarOperation.CByte);
        Available("CInt", ScalarOperation.CInt);
        Available("CLng", ScalarOperation.CLng);
        Available("CSng", ScalarOperation.CSng);
        Available("CDbl", ScalarOperation.CDbl);
        Available("CCur", ScalarOperation.CCur);
        Available("CDate", ScalarOperation.CDate);
        Available("CStr", ScalarOperation.CStr);
        Available("CVar", ScalarOperation.CVar, StaticType.Unknown);
        Available("CDec", ScalarOperation.CDec);
        Available("Int", ScalarOperation.Int);
        Available("Fix", ScalarOperation.Fix);
        Available("Abs", ScalarOperation.Abs);
        Available("Sgn", ScalarOperation.Sgn);
        Available("Round", ScalarOperation.Round);
        Available("IsNull", ScalarOperation.IsNull, StaticType.Boolean);
        Available("IsEmpty", ScalarOperation.IsEmpty, StaticType.Boolean);
        Available("IsError", ScalarOperation.IsError, StaticType.Boolean);
        Available("Len", ScalarOperation.Len);
        Available("UCase", ScalarOperation.UCase);
        Available("Chr", ScalarOperation.Chr);
        Available("Val", ScalarOperation.Val);
        Available("Left", ScalarOperation.Left);
        Available("Right", ScalarOperation.Right);
        Available("Mid", ScalarOperation.Mid);
        Available("InStr", default);
        Available("StrComp", default);
        Available("DateSerial", ScalarOperation.DateSerial);

        foreach (string name in new[]
        {
            "Nz", "CVErr", "IsArray", "Eval", "Environ", "CurDir", "Dir", "FileLen", "Command", "Join", "Split",
            "Filter", "UBound", "Randomize", "Log10", "CurrentUser", "CurrentDb", "CLngLng", "CLngPtr",
        })
        {
            names.Add(name, new Entry(FunctionAvailability.Unknown, default, StaticType.Unknown));
        }
        foreach (string name in new[] { "DLookup", "DSum", "DCount", "DMax", "DMin", "DFirst", "DLast", "DAvg" })
        {
            names.Add(name, new Entry(FunctionAvailability.Domain, default, StaticType.Unknown));
        }
        return names;
    }
}