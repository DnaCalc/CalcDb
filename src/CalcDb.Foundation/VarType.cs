namespace CalcDb.Foundation;

/// <summary>
/// Automation VARIANT tags (the platform's VARTYPE). Base types in the low 12 bits, modifiers above. These are the
/// platform's values, not CalcDb tags: no private tag is ever stored in a VARIANT (II-02 §1).
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Platform names.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1028:Enum storage should be Int32", Justification = "VARTYPE is 16 bits.")]
[Flags]
public enum VarType : ushort
{
    Empty = 0,
    Null = 1,
    I2 = 2,
    I4 = 3,
    R4 = 4,
    R8 = 5,
    Currency = 6,
    Date = 7,
    Bstr = 8,
    Dispatch = 9,
    Error = 10,
    Bool = 11,
    Variant = 12,
    Unknown = 13,
    Decimal = 14,
    I1 = 16,
    UI1 = 17,
    UI2 = 18,
    UI4 = 19,
    I8 = 20,
    UI8 = 21,
    Int = 22,
    UInt = 23,
    Record = 36,

    /// <summary>The mask of the base type.</summary>
    TypeMask = 0x0FFF,
    Array = 0x2000,
    ByRef = 0x4000,
}

/// <summary>Tag helpers.</summary>
public static class VarTypes
{
    public static VarType Base(VarType vt) => vt & VarType.TypeMask;

    public static bool IsArray(VarType vt) => (vt & VarType.Array) != 0;

    public static bool IsByRef(VarType vt) => (vt & VarType.ByRef) != 0;

    /// <summary>Whether the base type is an interface reference, whose copy runs foreign code (AddRef).</summary>
    public static bool IsObject(VarType vt) => Base(vt) is VarType.Dispatch or VarType.Unknown;

    /// <summary>Fixed-width scalars whose copy needs no allocation and no foreign code.</summary>
    public static bool IsPlainScalar(VarType vt) => Base(vt) is VarType.Empty or VarType.Null or VarType.I2 or VarType.I4 or VarType.R4 or VarType.R8
        or VarType.Currency or VarType.Date or VarType.Error or VarType.Bool or VarType.Decimal or VarType.I1 or VarType.UI1
        or VarType.UI2 or VarType.UI4 or VarType.I8 or VarType.UI8 or VarType.Int or VarType.UInt;
}
