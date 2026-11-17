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

/// <summary>A parameter of the dispatch view; PARAMFLAGS 1 in, 2 out, 16 optional, 32 has default.</summary>
internal sealed record ParamDecl(string Name, string Type, int Flags)
{
    public bool Optional => (Flags & 0x10) != 0;
}

/// <summary>
/// One member (II-13 F-138 MemberDecl): the member ID and invocation kind, the vtable byte offset of its dual entry,
/// the dispatch parameters (without the retval) and the dispatch return type.
/// </summary>
internal sealed record MemberDecl(int MemberId, string Name, InvokeKind Kind, int VtableOffset, string ReturnType, IReadOnlyList<ParamDecl> Parameters)
{
    public int Slot => VtableOffset / 8;
}

/// <summary>
/// A captured dual interface (II-13 F-138 InterfaceDecl): its IID, native vtable size and ordered entries beyond
/// IDispatch. <see cref="FillUnimplemented"/> fills every slot with a generated entry that clears its outputs and fails
/// with the named not-implemented outcome; implemented entries then replace their slots.
/// </summary>
internal sealed unsafe class InterfaceDecl(string name, Guid iid, int vtableBytes, IReadOnlyList<MemberDecl> members, delegate*<nint*, void> fillUnimplemented)
{
    public string Name { get; } = name;

    public Guid Iid { get; } = iid;

    public int VtableBytes { get; } = vtableBytes;

    public int SlotCount => VtableBytes / 8;

    public IReadOnlyList<MemberDecl> Members { get; } = members;

    public delegate*<nint*, void> FillUnimplemented { get; } = fillUnimplemented;

    public MemberDecl Member(string name, InvokeKind kind) =>
        Members.Single(m => m.Kind == kind && string.Equals(m.Name, name, StringComparison.Ordinal));

    public MemberDecl? BySlot(int slot) => Members.FirstOrDefault(m => m.Slot == slot);
}
