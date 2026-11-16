using System.Runtime.InteropServices;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Reads ITypeInfo/ITypeLib metadata of a live object (R-DAO-1 inventory, R-ACT-1 ACT-DECL). Public COM
/// metadata only: names, GUIDs, dispids, invoke kinds, vtable offsets and parameter types, as the type
/// library publishes them. Structure offsets are the documented Windows x64 layouts of TYPEATTR, FUNCDESC,
/// VARDESC, ELEMDESC and TLIBATTR.
/// </summary>
internal sealed unsafe class TypeDescriber(Dispatch dispatch, int lcid)
{
    private readonly List<string> _errors = [];

    public TypeEvent Describe(int seq, string path, string target, nint obj, bool wholeLibrary)
    {
        var types = new List<TypeView>();
        TypeLibraryInfo? library = null;
        nint typeInfo = GetTypeInfo(obj);
        if (typeInfo == 0)
        {
            _errors.Add("IDispatch::GetTypeInfo returned no type information.");
            return new TypeEvent { Seq = seq, ProgramPath = path, Target = target, Types = types, Errors = _errors };
        }
        try
        {
            nint lib = 0;
            uint index = 0;
            if (((delegate* unmanaged[Stdcall]<nint, nint*, uint*, int>)Vtbl(typeInfo)[18])(typeInfo, &lib, &index) >= 0 && lib != 0)
            {
                try
                {
                    library = DescribeLibrary(lib);
                    if (wholeLibrary)
                    {
                        uint count = ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(lib)[3])(lib);
                        for (uint i = 0; i < count; i++)
                        {
                            nint ti = 0;
                            if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(lib)[4])(lib, i, &ti) >= 0 && ti != 0)
                            {
                                try
                                {
                                    types.Add(DescribeType(ti, followDual: true));
                                }
                                finally
                                {
                                    Native.Release(ti);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Native.Release(lib);
                }
            }
            if (!wholeLibrary)
            {
                types.Add(DescribeType(typeInfo, followDual: true));
            }
        }
        finally
        {
            Native.Release(typeInfo);
        }
        return new TypeEvent { Seq = seq, ProgramPath = path, Target = target, Library = library, Types = types, Errors = _errors };
    }

    private nint GetTypeInfo(nint obj)
    {
        nint ti = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, int, nint*, int>)Vtbl(obj)[4])(obj, 0, lcid, &ti);
        return hr >= 0 ? ti : 0;
    }

    private static nint* Vtbl(nint p) => *(nint**)p;

    private TypeLibraryInfo DescribeLibrary(nint lib)
    {
        nint attr = 0;
        Guid guid = default;
        int lcidAttr = 0, syskind = 0, major = 0, minor = 0, flags = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(lib)[7])(lib, &attr) >= 0 && attr != 0)
        {
            guid = *(Guid*)attr;
            lcidAttr = *(int*)(attr + 16);
            syskind = *(int*)(attr + 20);
            major = *(ushort*)(attr + 24);
            minor = *(ushort*)(attr + 26);
            flags = *(ushort*)(attr + 28);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Vtbl(lib)[12])(lib, attr);
        }
        (string? name, string? help) = LibDocumentation(lib, -1);
        uint count = ((delegate* unmanaged[Stdcall]<nint, uint>)Vtbl(lib)[3])(lib);
        return new TypeLibraryInfo(name, help, guid.ToString("B").ToUpperInvariant(), major, minor, lcidAttr, syskind, flags, (int)count);
    }

    private TypeView DescribeType(nint ti, bool followDual)
    {
        nint attr = 0;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Vtbl(ti)[3])(ti, &attr) < 0 || attr == 0)
        {
            _errors.Add("GetTypeAttr failed.");
            return new TypeView { Name = "?", Guid = "?", TypeKind = -1, TypeFlags = 0, VtableBytes = 0, Implements = [], Functions = [], Variables = [] };
        }
        Guid guid = *(Guid*)attr;
        int typekind = *(int*)(attr + 44);
        ushort cFuncs = *(ushort*)(attr + 48);
        ushort cVars = *(ushort*)(attr + 50);
        ushort cImpl = *(ushort*)(attr + 52);
        ushort cbVft = *(ushort*)(attr + 54);
        ushort typeFlags = *(ushort*)(attr + 58);
        ((delegate* unmanaged[Stdcall]<nint, nint, void>)Vtbl(ti)[19])(ti, attr);

        (string? name, string? help) = Documentation(ti, -1);
        var functions = new List<FunctionInfo>();
        for (uint i = 0; i < cFuncs; i++)
        {
            FunctionInfo? f = Function(ti, i);
            if (f is not null)
            {
                functions.Add(f);
            }
        }
        var variables = new List<VariableInfo>();
        for (uint i = 0; i < cVars; i++)
        {
            VariableInfo? v = Variable(ti, i);
            if (v is not null)
            {
                variables.Add(v);
            }
        }
        var implements = new List<string>();
        TypeView? dual = null;
        for (int i = 0; i < cImpl; i++)
        {
            uint href = 0;
            if (((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Vtbl(ti)[8])(ti, (uint)i, &href) < 0)
            {
                continue;
            }
            int implFlags = 0;
            _ = ((delegate* unmanaged[Stdcall]<nint, uint, int*, int>)Vtbl(ti)[9])(ti, (uint)i, &implFlags);
            implements.Add($"{RefName(ti, href)} (flags {implFlags})");
        }
        const int TkindDispatch = 4, TypeflagDual = 0x40;
        if (followDual && typekind == TkindDispatch && (typeFlags & TypeflagDual) != 0)
        {
            uint href = 0;
            // GetRefTypeOfImplType(-1) on a dual dispinterface returns its vtable interface.
            if (((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Vtbl(ti)[8])(ti, unchecked((uint)-1), &href) >= 0)
            {
                nint vt = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(ti)[14])(ti, href, &vt) >= 0 && vt != 0)
                {
                    try
                    {
                        dual = DescribeType(vt, followDual: false);
                    }
                    finally
                    {
                        Native.Release(vt);
                    }
                }
            }
        }
        return new TypeView
        {
            Name = name ?? "?",
            Guid = guid.ToString("B").ToUpperInvariant(),
            TypeKind = typekind,
            TypeFlags = typeFlags,
            VtableBytes = cbVft,
            HelpString = help,
            Implements = implements,
            Functions = functions,
            Variables = variables,
            DualInterface = dual,
        };
    }

    private FunctionInfo? Function(nint ti, uint index)
    {
        nint fd = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(ti)[5])(ti, index, &fd) < 0 || fd == 0)
        {
            _errors.Add($"GetFuncDesc({index}) failed.");
            return null;
        }
        try
        {
            int memid = *(int*)fd;
            nint paramsPtr = *(nint*)(fd + 16);
            int funckind = *(int*)(fd + 24);
            int invkind = *(int*)(fd + 28);
            int callconv = *(int*)(fd + 32);
            short cParams = *(short*)(fd + 36);
            short cParamsOpt = *(short*)(fd + 38);
            short oVft = *(short*)(fd + 40);
            string ret = TypeName(ti, fd + 48);
            ushort funcFlags = *(ushort*)(fd + 80);
            string[] names = Names(ti, memid, cParams + 1);
            var parameters = new List<ParameterInfo>();
            for (int p = 0; p < cParams; p++)
            {
                nint elem = paramsPtr + (32 * p);
                ushort pflags = *(ushort*)(elem + 24);
                CapturedValue? def = null;
                nint pdex = *(nint*)(elem + 16);
                if ((pflags & 0x20) != 0 && pdex != 0)
                {
                    def = dispatch.CaptureValue((Variant*)(pdex + 8));
                }
                parameters.Add(new ParameterInfo(p + 1 < names.Length ? names[p + 1] : null, TypeName(ti, elem), pflags, def));
            }
            return new FunctionInfo(memid, names.Length > 0 ? names[0] : "?", invkind, funckind, callconv, oVft, funcFlags, ret, cParamsOpt, parameters);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Vtbl(ti)[20])(ti, fd);
        }
    }

    private VariableInfo? Variable(nint ti, uint index)
    {
        nint vd = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(ti)[6])(ti, index, &vd) < 0 || vd == 0)
        {
            _errors.Add($"GetVarDesc({index}) failed.");
            return null;
        }
        try
        {
            int memid = *(int*)vd;
            nint union = *(nint*)(vd + 16);
            string type = TypeName(ti, vd + 24);
            ushort flags = *(ushort*)(vd + 56);
            int varkind = *(int*)(vd + 60);
            const int VarConst = 2;
            CapturedValue? value = varkind == VarConst && union != 0 ? dispatch.CaptureValue((Variant*)union) : null;
            string[] names = Names(ti, memid, 1);
            return new VariableInfo(memid, names.Length > 0 ? names[0] : "?", type, varkind, flags, value);
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Vtbl(ti)[21])(ti, vd);
        }
    }

    /// <summary>A readable form of a TYPEDESC: VT name, with PTR/SAFEARRAY/USERDEFINED expanded.</summary>
    private string TypeName(nint ti, nint typedesc)
    {
        ushort vt = *(ushort*)(typedesc + 8);
        nint inner = *(nint*)typedesc;
        return vt switch
        {
            26 => "PTR " + TypeName(ti, inner),
            27 => "SAFEARRAY " + TypeName(ti, inner),
            29 => "USERDEFINED " + RefName(ti, (uint)(ulong)inner),
            24 => "VT_VOID",
            25 => "VT_HRESULT",
            _ => CapturedValue.NameOf(vt),
        };
    }

    private string RefName(nint ti, uint href)
    {
        nint rt = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Vtbl(ti)[14])(ti, href, &rt) < 0 || rt == 0)
        {
            return $"href:{href}";
        }
        try
        {
            return Documentation(rt, -1).Name ?? $"href:{href}";
        }
        finally
        {
            Native.Release(rt);
        }
    }

    private static string[] Names(nint ti, int memid, int max)
    {
        nint* names = stackalloc nint[Math.Max(max, 1)];
        uint got = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, uint, uint*, int>)Vtbl(ti)[7])(ti, memid, names, (uint)Math.Max(max, 1), &got) < 0)
        {
            return [];
        }
        var result = new string[got];
        for (int i = 0; i < got; i++)
        {
            result[i] = Marshal.PtrToStringBSTR(names[i]);
            Native.SysFreeString(names[i]);
        }
        return result;
    }

    private static (string? Name, string? Help) Documentation(nint ti, int memid)
    {
        nint name = 0, doc = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, nint*, uint*, nint*, int>)Vtbl(ti)[12])(ti, memid, &name, &doc, null, null) < 0)
        {
            return (null, null);
        }
        return (TakeBstr(name), TakeBstr(doc));
    }

    private static (string? Name, string? Help) LibDocumentation(nint lib, int index)
    {
        nint name = 0, doc = 0;
        if (((delegate* unmanaged[Stdcall]<nint, int, nint*, nint*, uint*, nint*, int>)Vtbl(lib)[9])(lib, index, &name, &doc, null, null) < 0)
        {
            return (null, null);
        }
        return (TakeBstr(name), TakeBstr(doc));
    }

    private static string? TakeBstr(nint b)
    {
        if (b == 0)
        {
            return null;
        }
        string s = Marshal.PtrToStringBSTR(b);
        Native.SysFreeString(b);
        return s;
    }
}
