namespace CalcDb.Com.Declarations;

/// <summary>INVOKEKIND as the type library declares it.</summary>
[Flags]
internal enum InvokeKind
{
    Method = 1,
    PropertyGet = 2,
    PropertyPut = 4,
    PropertyPutRef = 8,
}

/// <summary>What an unimplemented entry clears in the caller's retval cell before it fails (II-13 §3: null outputs on failure).</summary>
internal enum RetvalCell
{
    None,
    Byte,
    Int16,
    Int32,
    Pointer,
    Variant,
}

/// <summary>A declared default value (PARAMFLAG_FHASDEFAULT): its VARTYPE and exact integer, bits or text.</summary>
internal sealed record ParamDefault(int Vt, long? Integer = null, ulong? Bits = null, string? Text = null);

/// <summary>A parameter; PARAMFLAGS 1 in, 2 out, 8 retval, 16 optional, 32 has default. Type is the captured TYPEDESC text.</summary>
internal sealed record ParamDecl(string Name, string Type, int Flags, ParamDefault? Default = null)
{
    public bool Optional => (Flags & 0x10) != 0;
}

/// <summary>
/// One member of the dispatch view (II-13 F-138 MemberDecl): member ID, invocation kind, the vtable byte offset of its
/// dual entry, the dispatch parameters (without the retval) and the dispatch return type.
/// </summary>
internal sealed record MemberDecl(int MemberId, string Name, InvokeKind Kind, int VtableOffset, string ReturnType, IReadOnlyList<ParamDecl> Parameters)
{
    public int Slot => VtableOffset / 8;
}

/// <summary>One entry of the vtable view the interface itself declares (inherited entries belong to its base).</summary>
internal sealed record VtableEntryDecl(int MemberId, string Name, InvokeKind Kind, int VtableOffset, int FuncFlags, string ReturnType, IReadOnlyList<ParamDecl> Parameters);

/// <summary>
/// A captured dual interface (II-13 F-138 InterfaceDecl): IID, native vtable size, base interface, type flags of the
/// dispatch and vtable views, the flattened dispatch members beyond IDispatch and the vtable entries it declares.
/// <see cref="FillUnimplemented"/> fills every slot with a generated entry that clears its outputs and fails with the
/// named not-implemented outcome; implemented entries then replace their slots.
/// </summary>
internal sealed unsafe class InterfaceDecl(string name, Guid iid, int vtableBytes, string baseName, int dispatchFlags, int vtableFlags,
    IReadOnlyList<MemberDecl> members, IReadOnlyList<VtableEntryDecl> entries, delegate*<nint*, void> fillUnimplemented)
{
    public string Name { get; } = name;

    public Guid Iid { get; } = iid;

    public int VtableBytes { get; } = vtableBytes;

    public int SlotCount => VtableBytes / 8;

    /// <summary>The base of the vtable view: IDispatch or another interface of the library.</summary>
    public string BaseName { get; } = baseName;

    public int DispatchFlags { get; } = dispatchFlags;

    public int VtableFlags { get; } = vtableFlags;

    public IReadOnlyList<MemberDecl> Members { get; } = members;

    public IReadOnlyList<VtableEntryDecl> Entries { get; } = entries;

    public delegate*<nint*, void> FillUnimplemented { get; } = fillUnimplemented;

    public MemberDecl Member(string name, InvokeKind kind) =>
        Members.Single(m => m.Kind == kind && string.Equals(m.Name, name, StringComparison.Ordinal));

    public MemberDecl? BySlot(int slot) => Members.FirstOrDefault(m => m.Slot == slot);
}

internal sealed record EnumDecl(string Name, Guid Guid, IReadOnlyList<(string Name, int MemberId, int Value)> Constants);

internal sealed record CoclassDecl(string Name, Guid Guid, int TypeFlags, IReadOnlyList<(string Interface, int ImplFlags)> Implements);

/// <summary>The captured library identity and its types (II-13 F-138 TypeLibraryImage).</summary>
internal sealed record LibraryDecl(string Name, Guid Guid, ushort Major, ushort Minor, int Lcid, IReadOnlyList<InterfaceDecl> Interfaces, IReadOnlyList<EnumDecl> Enums, IReadOnlyList<CoclassDecl> Coclasses);
