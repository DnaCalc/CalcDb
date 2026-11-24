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

/// <summary>Expression-layer functions evaluated directly in <see cref="BoundExpression"/> rather than as one <see cref="ScalarOperation"/>.</summary>
internal enum ExprFunction : byte
{
    None,
    InStr,
    StrComp,
    IIf,
    Switch,
    Choose,
    String,
    StringDollar,
    VarType,
    TypeName,
    Space,
    SpaceDollar,
    Trim,
    TrimDollar,
    LTrim,
    LTrimDollar,
    RTrim,
    RTrimDollar,
    ChrW,
    ChrWDollar,
    ChrDollar,
    Asc,
    AscW,
    Sqr,
    Exp,
    Log,
    Atn,
    CVDate,
    LeftDollar,
    RightDollar,
    MidDollar,
    UCaseDollar,
    LCase,
    LCaseDollar,
    LenB,
    LeftB,
    RightB,
    MidB,
    InStrB,
    Hex,
    HexDollar,
    Oct,
    OctDollar,
    IsNumeric,
    IsDate,
    Replace,
    StrReverse,
}

/// <summary>
/// The functions an expression may call (II-10 §13: an inventory of executable descriptors). Available: the kernel's
/// functions and the expression-layer functions decided by retained records (qualified.md "R-EXPR-1 Branch B,
/// EXPR-RUNTIME"; r-expr-1, r-expr-1b, r-wire, r-def-expr-sql, r-q3-sql, r-q4-values). Unknown: the names standalone ACE
/// lacks (qualified.md "function availability": Nz, CVErr, …; r-expr-1b/str-chr-asc-range ChrB; r-expr-1b/num-sqr-exp-log-trig
/// Atan2, Pi, Sinh, Sec, Tanh, Cot). Domain: the domain aggregates (DMax asked in Find). Any other name is not in this
/// inventory: the binder names it instead of guessing whether ACE knows it.
/// </summary>
public static class FunctionInventory
{
    /// <summary>A function's availability, its kernel operation (null for expression-layer, Unknown, and Domain entries) and its result's static type.</summary>
    public readonly record struct Entry(FunctionAvailability Availability, ScalarOperation? Operation, StaticType Type);

    private readonly record struct Record(Entry Entry, ExprFunction Function);

    private static readonly Dictionary<string, Record> Names = Build();

    public static Entry? Resolve(string name) => Names.TryGetValue(name, out Record r) ? r.Entry : null;

    internal static ExprFunction ResolveExpressionFunction(string name) => Names.TryGetValue(name, out Record r) ? r.Function : ExprFunction.None;

    private static Dictionary<string, Record> Build()
    {
        var names = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
        void Available(string name, ScalarOperation op, StaticType type = StaticType.Value) =>
            names.Add(name, new Record(new Entry(FunctionAvailability.Available, op, type), ExprFunction.None));
        void Expr(string name, ExprFunction fn, StaticType type = StaticType.Value) =>
            names.Add(name, new Record(new Entry(FunctionAvailability.Available, null, type), fn));

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
        Available("DateSerial", ScalarOperation.DateSerial);

        Expr("InStr", ExprFunction.InStr);
        Expr("StrComp", ExprFunction.StrComp);
        Expr("IIf", ExprFunction.IIf, StaticType.Unknown);
        Expr("Switch", ExprFunction.Switch, StaticType.Unknown);
        Expr("Choose", ExprFunction.Choose, StaticType.Unknown);
        Expr("String", ExprFunction.String);
        Expr("String$", ExprFunction.StringDollar);
        Expr("VarType", ExprFunction.VarType);
        Expr("TypeName", ExprFunction.TypeName);
        Expr("Space", ExprFunction.Space);
        Expr("Space$", ExprFunction.SpaceDollar);
        Expr("Trim", ExprFunction.Trim);
        Expr("Trim$", ExprFunction.TrimDollar);
        Expr("LTrim", ExprFunction.LTrim);
        Expr("LTrim$", ExprFunction.LTrimDollar);
        Expr("RTrim", ExprFunction.RTrim);
        Expr("RTrim$", ExprFunction.RTrimDollar);
        Expr("ChrW", ExprFunction.ChrW);
        Expr("ChrW$", ExprFunction.ChrWDollar);
        Expr("Chr$", ExprFunction.ChrDollar);
        Expr("Asc", ExprFunction.Asc);
        Expr("AscW", ExprFunction.AscW);
        Expr("Sqr", ExprFunction.Sqr);
        Expr("Exp", ExprFunction.Exp);
        Expr("Log", ExprFunction.Log);
        Expr("Atn", ExprFunction.Atn);
        Expr("CVDate", ExprFunction.CVDate);
        Expr("Left$", ExprFunction.LeftDollar);
        Expr("Right$", ExprFunction.RightDollar);
        Expr("Mid$", ExprFunction.MidDollar);
        Expr("UCase$", ExprFunction.UCaseDollar);
        Expr("LCase", ExprFunction.LCase);
        Expr("LCase$", ExprFunction.LCaseDollar);
        Expr("LenB", ExprFunction.LenB);
        Expr("LeftB", ExprFunction.LeftB);
        Expr("RightB", ExprFunction.RightB);
        Expr("MidB", ExprFunction.MidB);
        Expr("InStrB", ExprFunction.InStrB);
        Expr("Hex", ExprFunction.Hex);
        Expr("Hex$", ExprFunction.HexDollar);
        Expr("Oct", ExprFunction.Oct);
        Expr("Oct$", ExprFunction.OctDollar);
        Expr("IsNumeric", ExprFunction.IsNumeric, StaticType.Boolean);
        Expr("IsDate", ExprFunction.IsDate, StaticType.Boolean);
        Expr("Replace", ExprFunction.Replace);
        Expr("StrReverse", ExprFunction.StrReverse);

        foreach (string name in new[]
        {
            "Nz", "CVErr", "IsArray", "Eval", "Environ", "CurDir", "Dir", "FileLen", "Command", "Join", "Split",
            "Filter", "UBound", "Randomize", "Log10", "CurrentUser", "CurrentDb", "CLngLng", "CLngPtr",
            "ChrB", "Atan2", "Pi", "Sinh", "Sec", "Tanh", "Cot",
        })
        {
            names.Add(name, new Record(new Entry(FunctionAvailability.Unknown, null, StaticType.Unknown), ExprFunction.None));
        }
        foreach (string name in new[] { "DLookup", "DSum", "DCount", "DMax", "DMin", "DFirst", "DLast", "DAvg" })
        {
            names.Add(name, new Record(new Entry(FunctionAvailability.Domain, null, StaticType.Unknown), ExprFunction.None));
        }
        return names;
    }
}