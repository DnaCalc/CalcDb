using System.Runtime.InteropServices;
using CalcDb.Com.Declarations;

namespace CalcDb.Com.Com;

/// <summary>
/// CalcDb's own type information (II-13 §§2, 11): an in-memory type library built through ICreateTypeLib2 from the
/// generated declarations, never saved or registered. Dual interfaces are created as TKIND_INTERFACE with their captured
/// flags on IDispatch (stdole2), so each has its dispatch view and, through GetRefTypeOfImplType(-1), its vtable view.
/// Each library keeps the compatibility identity the clients consume (DAO 12.0, ADODB 6.1). Each is built once, on first
/// use, and retained for the module's lifetime.
/// </summary>
internal static unsafe partial class TypeLibrary
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<LibraryDecl, (nint Library, int Failure)> Built = new(ReferenceEqualityComparer.Instance);

    /// <summary>The step that failed when the library could not be built (diagnostics), or null.</summary>
    public static string? BuildFailureStep { get; private set; }

    /// <summary>The ITypeInfo of an interface's dispatch view, retained for the caller, or the failure HRESULT.</summary>
    public static int TypeInfoOf(InterfaceDecl declaration, nint* result)
    {
        *result = 0;
        nint library = Library(DeclarationIndex.LibraryOf(declaration), out int failure);
        if (library == 0)
        {
            return failure;
        }
        Guid iid = declaration.Iid;
        return ((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(library)[6])(library, &iid, result); // ITypeLib::GetTypeInfoOfGuid
    }

    private static nint Library(LibraryDecl decl, out int failure)
    {
        lock (Gate)
        {
            if (!Built.TryGetValue(decl, out (nint Library, int Failure) built))
            {
                built.Failure = Build(decl, out built.Library);
                Built[decl] = built;
            }
            failure = built.Failure;
            return built.Library;
        }
    }

    private static nint* Vt(nint unknown) => *(nint**)unknown;

    private static void Check(int hr, string step)
    {
        if (hr < 0)
        {
            throw new TypeLibraryException(step, hr);
        }
    }

    private static int Build(LibraryDecl decl, out nint library)
    {
        library = 0;
        nint create = 0;
        nint stdole = 0;
        var infos = new Dictionary<string, nint>(StringComparer.Ordinal);
        try
        {
            Check(CreateTypeLib2(3 /* SYS_WIN64 */, decl.Name + ".calcdb.tlb", out create), "CreateTypeLib2");
            Guid guid = decl.Guid;
            Check(Call(create, 6, &guid), "SetGuid");
            Check(CallText(create, 4, decl.Name), "SetName");
            Check(((delegate* unmanaged<nint, ushort, ushort, int>)Vt(create)[5])(create, decl.Major, decl.Minor), "SetVersion");
            Check(((delegate* unmanaged<nint, uint, int>)Vt(create)[10])(create, (uint)decl.Lcid), "SetLcid");
            Check(LoadTypeLibEx("stdole2.tlb", 2 /* REGKIND_NONE */, out stdole), "LoadTypeLibEx stdole2");
            Guid dispatchIid = ComIids.IDispatch;
            nint dispatchInfo = 0;
            Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(stdole)[6])(stdole, &dispatchIid, &dispatchInfo), "stdole IDispatch");
            infos["IDispatch"] = dispatchInfo;

            // Pass 1: every type exists, so any can be referenced.
            foreach (EnumDecl e in decl.Enums)
            {
                infos[e.Name] = NewType(create, e.Name, 0 /* TKIND_ENUM */, e.Guid, 0);
            }
            foreach (InterfaceDecl i in decl.Interfaces)
            {
                infos[i.Name] = NewType(create, i.Name, 3 /* TKIND_INTERFACE */, i.Iid, i.VtableFlags & ~0x1000 /* FDISPATCHABLE is derived */);
            }
            foreach (CoclassDecl c in decl.Coclasses)
            {
                infos[c.Name] = NewType(create, c.Name, 5 /* TKIND_COCLASS */, c.Guid, c.TypeFlags);
            }
            foreach (AliasDecl a in decl.AliasList)
            {
                infos[a.Name] = NewType(create, a.Name, 6 /* TKIND_ALIAS */, a.Guid, a.TypeFlags);
            }

            // Pass 2: members and relations.
            foreach (AliasDecl a in decl.AliasList)
            {
                nint ti = infos[a.Name];
                var arena = new List<nint>();
                try
                {
                    TypeDesc target = TypeOf(a.Target, ti, infos, bytes =>
                    {
                        nint block = (nint)NativeMemory.AllocZeroed((nuint)bytes);
                        arena.Add(block);
                        return block;
                    });
                    Check(((delegate* unmanaged<nint, TypeDesc*, int>)Vt(ti)[17])(ti, &target), a.Name + " SetTypeDescAlias");
                }
                finally
                {
                    foreach (nint block in arena)
                    {
                        NativeMemory.Free((void*)block);
                    }
                }
            }
            foreach (EnumDecl e in decl.Enums)
            {
                nint ti = infos[e.Name];
                for (int k = 0; k < e.Constants.Count; k++)
                {
                    AddConstant(ti, k, e.Constants[k].MemberId, e.Constants[k].Name, e.Constants[k].Value);
                }
            }
            foreach (InterfaceDecl i in decl.Interfaces)
            {
                nint ti = infos[i.Name];
                uint baseRef = Reference(ti, infos[i.BaseName]);
                Check(((delegate* unmanaged<nint, uint, uint, int>)Vt(ti)[10])(ti, 0, baseRef), $"{i.Name} AddImplType");
                for (int k = 0; k < i.Entries.Count; k++)
                {
                    AddFunction(ti, k, i.Entries[k], infos, i.Name);
                }
            }
            foreach (CoclassDecl c in decl.Coclasses)
            {
                nint ti = infos[c.Name];
                for (int k = 0; k < c.Implements.Count; k++)
                {
                    uint href = Reference(ti, infos[c.Implements[k].Interface]);
                    Check(((delegate* unmanaged<nint, uint, uint, int>)Vt(ti)[10])(ti, (uint)k, href), $"{c.Name} AddImplType");
                    Check(((delegate* unmanaged<nint, uint, int, int>)Vt(ti)[11])(ti, (uint)k, c.Implements[k].ImplFlags), $"{c.Name} SetImplTypeFlags");
                }
            }

            // Lay out bases before the interfaces derived from them.
            var laidOut = new HashSet<string>(StringComparer.Ordinal) { "IDispatch" };
            foreach (EnumDecl e in decl.Enums)
            {
                LayOut(infos[e.Name], e.Name, laidOut);
            }
            foreach (AliasDecl a in decl.AliasList)
            {
                LayOutAlias(a, decl, infos, laidOut);
            }
            foreach (InterfaceDecl i in decl.Interfaces)
            {
                LayOutInterface(i, decl, infos, laidOut);
            }
            foreach (CoclassDecl c in decl.Coclasses)
            {
                LayOut(infos[c.Name], c.Name, laidOut);
            }

            Guid typeLibIid = new("00020402-0000-0000-C000-000000000046");
            nint lib = 0;
            Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(create)[0])(create, &typeLibIid, &lib), "QueryInterface ITypeLib");
            library = lib;
            return HResults.S_OK;
        }
        catch (TypeLibraryException e)
        {
            BuildFailureStep = e.Message;
            return e.HResult;
        }
        finally
        {
            foreach (nint ti in infos.Values)
            {
                Release(ti);
            }
            if (stdole != 0)
            {
                Release(stdole);
            }
            if (create != 0)
            {
                Release(create);
            }
        }
    }

    private static void LayOutInterface(InterfaceDecl i, LibraryDecl decl, Dictionary<string, nint> infos, HashSet<string> laidOut)
    {
        if (laidOut.Contains(i.Name))
        {
            return;
        }
        if (!laidOut.Contains(i.BaseName))
        {
            LayOutInterface(decl.Interfaces.Single(b => b.Name == i.BaseName), decl, infos, laidOut);
        }
        LayOut(infos[i.Name], i.Name, laidOut);
    }

    /// <summary>An alias after the alias it names (ADO: PositionEnum_Param names ADO_LONGPTR).</summary>
    private static void LayOutAlias(AliasDecl a, LibraryDecl decl, Dictionary<string, nint> infos, HashSet<string> laidOut)
    {
        if (laidOut.Contains(a.Name))
        {
            return;
        }
        if (a.Target.StartsWith("USERDEFINED ", StringComparison.Ordinal) && decl.AliasList.FirstOrDefault(x => x.Name == a.Target[12..]) is { } inner)
        {
            LayOutAlias(inner, decl, infos, laidOut);
        }
        LayOut(infos[a.Name], a.Name, laidOut);
    }

    private static void LayOut(nint createInfo, string name, HashSet<string> laidOut)
    {
        Check(((delegate* unmanaged<nint, int>)Vt(createInfo)[25])(createInfo), name + " LayOut");
        laidOut.Add(name);
    }

    /// <summary>A new type; the result is its ITypeInfo (the ICreateTypeInfo is queried for it when referenced).</summary>
    private static nint NewType(nint create, string name, int kind, Guid guid, int flags)
    {
        nint ti = 0;
        fixed (char* n = name)
        {
            Check(((delegate* unmanaged<nint, char*, int, nint*, int>)Vt(create)[3])(create, n, kind, &ti), name + " CreateTypeInfo");
        }
        Check(Call(ti, 3, &guid), name + " SetGuid");
        if (flags != 0)
        {
            Check(((delegate* unmanaged<nint, uint, int>)Vt(ti)[4])(ti, (uint)flags), name + " SetTypeFlags");
        }
        return ti;
    }

    /// <summary>The href of <paramref name="target"/> as seen from <paramref name="from"/> (ICreateTypeInfo::AddRefTypeInfo).</summary>
    private static uint Reference(nint from, nint target)
    {
        Guid typeInfoIid = new("00020401-0000-0000-C000-000000000046");
        nint info = 0;
        Check(((delegate* unmanaged<nint, Guid*, nint*, int>)Vt(target)[0])(target, &typeInfoIid, &info), "QueryInterface ITypeInfo");
        try
        {
            uint href = 0;
            Check(((delegate* unmanaged<nint, nint, uint*, int>)Vt(from)[8])(from, info, &href), "AddRefTypeInfo");
            return href;
        }
        finally
        {
            Release(info);
        }
    }

    private static void AddConstant(nint ti, int index, int memberId, string name, int value)
    {
        var desc = (VarDesc*)NativeMemory.AllocZeroed((nuint)sizeof(VarDesc));
        var cell = (Foundation.Windows.NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(Foundation.Windows.NativeVariant));
        try
        {
            cell->Vt = 3; // VT_I4
            *(int*)&cell->Int64 = value;
            desc->MemberId = memberId;
            desc->Value = (nint)cell;
            desc->Element.Type.Vt = 3;
            desc->VarKind = 2; // VAR_CONST
            Check(((delegate* unmanaged<nint, uint, VarDesc*, int>)Vt(ti)[14])(ti, (uint)index, desc), name + " AddVarDesc");
            fixed (char* n = name)
            {
                Check(((delegate* unmanaged<nint, uint, char*, int>)Vt(ti)[16])(ti, (uint)index, n), name + " SetVarName");
            }
        }
        finally
        {
            NativeMemory.Free(cell);
            NativeMemory.Free(desc);
        }
    }

    private static void AddFunction(nint ti, int index, VtableEntryDecl entry, Dictionary<string, nint> infos, string owner)
    {
        var arena = new List<nint>();
        nint Alloc(int bytes)
        {
            nint p = (nint)NativeMemory.AllocZeroed((nuint)bytes);
            arena.Add(p);
            return p;
        }
        var bstrs = new List<nint>();
        try
        {
            int count = entry.Parameters.Count;
            var desc = (FuncDesc*)Alloc(sizeof(FuncDesc));
            var parameters = count == 0 ? null : (ElemDesc*)Alloc(sizeof(ElemDesc) * count);
            for (int k = 0; k < count; k++)
            {
                ParamDecl p = entry.Parameters[k];
                parameters[k].Type = TypeOf(p.Type, ti, infos, Alloc);
                parameters[k].Param.Flags = (ushort)p.Flags;
                if (p.Default is { } d)
                {
                    var ex = (ParamDescEx*)Alloc(sizeof(ParamDescEx));
                    ex->Size = (uint)sizeof(ParamDescEx);
                    ex->Default.Vt = (ushort)d.Vt;
                    if (d.Text is { } text)
                    {
                        nint b = Foundation.Windows.OwnedVariant.FromText(text).TransferBstr();
                        bstrs.Add(b);
                        ex->Default.Pointer = b;
                    }
                    else if (d.Bits is { } bits)
                    {
                        ex->Default.Int64 = (long)bits;
                    }
                    else if (d.Integer is { } integer)
                    {
                        ex->Default.Int64 = integer;
                    }
                    parameters[k].Param.Extension = (nint)ex;
                }
            }
            desc->MemberId = entry.MemberId;
            desc->Parameters = (nint)parameters;
            desc->FuncKind = 1; // FUNC_PUREVIRTUAL
            desc->InvokeKind = (int)entry.Kind;
            desc->CallConv = 4; // CC_STDCALL
            desc->ParameterCount = (short)count;
            desc->VtableOffset = (short)entry.VtableOffset;
            desc->Result.Type = TypeOf(entry.ReturnType, ti, infos, Alloc);
            desc->FuncFlags = (ushort)entry.FuncFlags;
            Check(((delegate* unmanaged<nint, uint, FuncDesc*, int>)Vt(ti)[9])(ti, (uint)index, desc), $"{owner}.{entry.Name} AddFuncDesc");

            // Names: the function, then its parameters up to the first unnamed one. A property put's value parameter
            // takes no name (SetFuncAndParamNames).
            bool put = entry.Kind is InvokeKind.PropertyPut or InvokeKind.PropertyPutRef;
            var names = new List<string> { entry.Name };
            names.AddRange(entry.Parameters.Take(put ? count - 1 : count).Select(p => p.Name).TakeWhile(n => n.Length > 0));
            var handles = names.Select(n => GCHandle.Alloc(n, GCHandleType.Pinned)).ToArray();
            try
            {
                nint* pointers = stackalloc nint[names.Count];
                for (int k = 0; k < names.Count; k++)
                {
                    pointers[k] = handles[k].AddrOfPinnedObject();
                }
                Check(((delegate* unmanaged<nint, uint, nint*, uint, int>)Vt(ti)[15])(ti, (uint)index, pointers, (uint)names.Count), $"{owner}.{entry.Name} SetFuncAndParamNames");
            }
            finally
            {
                foreach (GCHandle h in handles)
                {
                    h.Free();
                }
            }
        }
        finally
        {
            foreach (nint b in bstrs)
            {
                Marshal.FreeBSTR(b);
            }
            foreach (nint p in arena)
            {
                NativeMemory.Free((void*)p);
            }
        }
    }

    /// <summary>A TYPEDESC for the captured type text: "PTR …", "SAFEARRAY …", "USERDEFINED name" or a VT name.</summary>
    private static TypeDesc TypeOf(string text, nint ti, Dictionary<string, nint> infos, Func<int, nint> alloc)
    {
        if (text.StartsWith("PTR ", StringComparison.Ordinal) || text.StartsWith("SAFEARRAY ", StringComparison.Ordinal))
        {
            bool pointer = text[0] == 'P';
            var inner = (TypeDesc*)alloc(sizeof(TypeDesc));
            *inner = TypeOf(text[(pointer ? 4 : 10)..], ti, infos, alloc);
            return new TypeDesc { Inner = (nint)inner, Vt = (ushort)(pointer ? 26 : 27) };
        }
        if (text.StartsWith("USERDEFINED ", StringComparison.Ordinal))
        {
            string name = text[12..];
            uint href = Reference(ti, infos.TryGetValue(name, out nint target) ? target : throw new TypeLibraryException("unknown type " + name, HResults.TYPE_E_ELEMENTNOTFOUND));
            return new TypeDesc { Inner = (nint)href, Vt = 29 };
        }
        return new TypeDesc { Vt = VarTypeNamed(text) };
    }

    private static ushort VarTypeNamed(string name) => name switch
    {
        "VT_EMPTY" => 0,
        "VT_NULL" => 1,
        "VT_I2" => 2,
        "VT_I4" => 3,
        "VT_R4" => 4,
        "VT_R8" => 5,
        "VT_CY" => 6,
        "VT_DATE" => 7,
        "VT_BSTR" => 8,
        "VT_DISPATCH" => 9,
        "VT_ERROR" => 10,
        "VT_BOOL" => 11,
        "VT_VARIANT" => 12,
        "VT_UNKNOWN" => 13,
        "VT_DECIMAL" => 14,
        "VT_I1" => 16,
        "VT_UI1" => 17,
        "VT_UI2" => 18,
        "VT_UI4" => 19,
        "VT_I8" => 20,
        "VT_UI8" => 21,
        "VT_INT" => 22,
        "VT_UINT" => 23,
        "VT_VOID" => 24,
        "VT_HRESULT" => 25,
        "VT_LPSTR" => 30,
        "VT_LPWSTR" => 31,
        _ => throw new TypeLibraryException("unknown type " + name, HResults.TYPE_E_ELEMENTNOTFOUND),
    };

    private static int Call(nint unknown, int slot, Guid* guid) => ((delegate* unmanaged<nint, Guid*, int>)Vt(unknown)[slot])(unknown, guid);

    private static int CallText(nint unknown, int slot, string text)
    {
        fixed (char* t = text)
        {
            return ((delegate* unmanaged<nint, char*, int>)Vt(unknown)[slot])(unknown, t);
        }
    }

    private static void Release(nint unknown) => ((delegate* unmanaged<nint, uint>)Vt(unknown)[2])(unknown);

    [StructLayout(LayoutKind.Sequential)]
    private struct TypeDesc
    {
        public nint Inner;
        public ushort Vt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamDesc
    {
        public nint Extension;
        public ushort Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ElemDesc
    {
        public TypeDesc Type;
        public ParamDesc Param;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParamDescEx
    {
        public uint Size;
        public Foundation.Windows.NativeVariant Default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FuncDesc
    {
        public int MemberId;
        public nint Scodes;
        public nint Parameters;
        public int FuncKind;
        public int InvokeKind;
        public int CallConv;
        public short ParameterCount;
        public short OptionalCount;
        public short VtableOffset;
        public short ScodeCount;
        public ElemDesc Result;
        public ushort FuncFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VarDesc
    {
        public int MemberId;
        public nint Schema;
        public nint Value;
        public ElemDesc Element;
        public ushort VarFlags;
        public int VarKind;
    }

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateTypeLib2(int sysKind, string file, out nint createTypeLib);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LoadTypeLibEx(string file, int regKind, out nint typeLib);
}

/// <summary>A failed step of building the type library: the step and its HRESULT, published as the GetTypeInfo failure.</summary>
internal sealed class TypeLibraryException(string step, int hresult) : Exception($"{step} failed with 0x{hresult:X8}")
{
    public new int HResult { get; } = hresult;
}