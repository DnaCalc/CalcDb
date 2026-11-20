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
/// <see cref="FillEntries"/> fills every declared slot with its generated typed entry (TypedCall), which reaches the
/// member's operation or fails with the named not-implemented outcome.
/// </summary>
internal sealed unsafe class InterfaceDecl(string name, Guid iid, int vtableBytes, string baseName, int dispatchFlags, int vtableFlags,
    IReadOnlyList<MemberDecl> members, IReadOnlyList<VtableEntryDecl> entries, delegate*<nint*, void> fillEntries)
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

    public delegate*<nint*, void> FillEntries { get; } = fillEntries;

    /// <summary>
    /// The members of a name, case-insensitive, in declaration order (GetIDsOfNames; task fbh8): a dictionary built on
    /// first use, looked up by the caller's characters. Empty when no member has the name.
    /// </summary>
    public MemberDecl[] MembersNamed(ReadOnlySpan<char> name) =>
        (_byName ??= BuildByName()).GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out MemberDecl[]? found) ? found : [];

    private Dictionary<string, MemberDecl[]>? _byName;

    private Dictionary<string, MemberDecl[]> BuildByName()
    {
        var byName = new Dictionary<string, MemberDecl[]>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, MemberDecl> g in Members.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            byName[g.Key] = [.. g];
        }
        return byName;
    }

    public MemberDecl Member(string name, InvokeKind kind) =>
        Members.Single(m => m.Kind == kind && string.Equals(m.Name, name, StringComparison.Ordinal));

    private MemberDecl?[]? _bySlot;

    /// <summary>The dispatch member of a vtable slot, from a table built once (no search per call, The Plan §3.2).</summary>
    public MemberDecl? BySlot(int slot)
    {
        MemberDecl?[] table = _bySlot ??= BuildSlotTable();
        return slot >= 0 && slot < table.Length ? table[slot] : null;
    }

    private MemberDecl?[] BuildSlotTable()
    {
        var table = new MemberDecl?[SlotCount];
        foreach (MemberDecl m in Members)
        {
            if (m.Slot >= 0 && m.Slot < table.Length)
            {
                table[m.Slot] ??= m; // the first declared member of a slot, as the search found it
            }
        }
        return table;
    }
}

internal sealed record EnumDecl(string Name, Guid Guid, IReadOnlyList<(string Name, int MemberId, int Value)> Constants);

internal sealed record CoclassDecl(string Name, Guid Guid, int TypeFlags, IReadOnlyList<(string Interface, int ImplFlags)> Implements);

/// <summary>A type alias (TKIND_ALIAS) and the captured type text it stands for.</summary>
internal sealed record AliasDecl(string Name, Guid Guid, int TypeFlags, string Target);

/// <summary>The captured library identity and its types (II-13 F-138 TypeLibraryImage).</summary>
internal sealed record LibraryDecl(string Name, Guid Guid, ushort Major, ushort Minor, int Lcid, IReadOnlyList<InterfaceDecl> Interfaces, IReadOnlyList<EnumDecl> Enums, IReadOnlyList<CoclassDecl> Coclasses, IReadOnlyList<AliasDecl>? Aliases = null)
{
    public IReadOnlyList<AliasDecl> AliasList => Aliases ?? [];
}