using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CalcDb.Com.Declarations;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Com;

/// <summary>An operation's result: an owned value, nothing, or a failure. Both adapters reach the same operation (II-13 §3).</summary>
internal readonly record struct CallResult(OwnedVariant? Value, Failure? Failure)
{
    public static CallResult Of(OwnedVariant value) => new(value, null);

    public static CallResult Fail(Failure failure) => new(null, failure);
}

/// <summary>A typed object operation. <paramref name="args"/> holds one borrowed VARIANT pointer per declared parameter (0 when omitted).</summary>
internal delegate CallResult Operation(ComObject self, ReadOnlySpan<nint> args);

/// <summary>DISPPARAMS, Windows x64 layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DispParams
{
    public nint Args;
    public nint NamedArgs;
    public uint ArgCount;
    public uint NamedArgCount;
}

/// <summary>
/// A wrapper with a dual interface: its declaration, and the operations it implements by (member ID, invoke kind).
/// The dispatch adapter binds its arguments and reaches the operation; a declared member without an operation fails
/// with the named not-implemented outcome.
/// </summary>
internal abstract unsafe class DispatchObject(InterfaceDecl declaration, nint vtable) : ComObject(vtable)
{
    public InterfaceDecl Declaration { get; } = declaration;

    protected abstract Operation? Find(MemberDecl member);

    public CallResult Run(MemberDecl member, ReadOnlySpan<nint> args)
    {
        if (Find(member) is not { } op)
        {
            return CallResult.Fail(Failure.NotImplemented($"{Declaration.Name}.{member.Name}"));
        }
        try
        {
            return op(this, args);
        }
        catch (Exception e)
        {
            return CallResult.Fail(Failure.Defect(e));
        }
    }

    /// <summary>
    /// GetIDsOfNames: the member name (case-insensitive), then parameter names as their positions (R-ACT-1 ACT-ABI:
    /// DAO named arguments are numbered from 0). An unknown name gives -1 in its cell and DISP_E_UNKNOWNNAME.
    /// </summary>
    public int GetIds(char** names, uint count, int* ids)
    {
        if (count == 0)
        {
            return HResults.S_OK;
        }
        string name = new(names[0]);
        MemberDecl[] members = Declaration.Members.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        int hr = HResults.S_OK;
        ids[0] = members.Length == 0 ? -1 : members[0].MemberId;
        if (members.Length == 0)
        {
            hr = HResults.DISP_E_UNKNOWNNAME;
        }
        for (uint i = 1; i < count; i++)
        {
            string p = new(names[i]);
            int index = -1;
            foreach (MemberDecl m in members)
            {
                index = IndexOf(m.Parameters, p);
                if (index >= 0)
                {
                    break;
                }
            }
            ids[i] = index;
            if (index < 0)
            {
                hr = HResults.DISP_E_UNKNOWNNAME;
            }
        }
        return hr;
    }

    private static int IndexOf(IReadOnlyList<ParamDecl> parameters, string name)
    {
        for (int k = 0; k < parameters.Count; k++)
        {
            if (string.Equals(parameters[k].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return k;
            }
        }
        return -1;
    }

    public int Invoke(int dispId, ushort flags, DispParams* parameters, NativeVariant* result, ExcepInfo* info, uint* argError)
    {
        MemberDecl? member = Choose(dispId, flags);
        if (member is null)
        {
            return HResults.DISP_E_MEMBERNOTFOUND;
        }
        int declared = member.Parameters.Count;
        Span<nint> args = declared <= 8 ? stackalloc nint[declared] : new nint[declared];
        args.Clear();
        uint count = parameters is null ? 0 : parameters->ArgCount;
        uint named = parameters is null ? 0 : parameters->NamedArgCount;
        if (named > count)
        {
            return HResults.E_INVALIDARG;
        }
        if (count > declared)
        {
            return HResults.DISP_E_BADPARAMCOUNT;
        }
        NativeVariant* rgvarg = count == 0 ? null : (NativeVariant*)parameters->Args;
        int* namedIds = named == 0 ? null : (int*)parameters->NamedArgs;
        uint positional = count - named;
        for (uint i = 0; i < positional; i++)
        {
            args[(int)i] = (nint)(rgvarg + (count - 1 - i));
        }
        for (uint j = 0; j < named; j++)
        {
            // A property put's value is the named argument DISPID_PROPERTYPUT (-3): the last declared parameter.
            int target = namedIds[j] == -3 ? declared - 1 : namedIds[j];
            if (target < 0 || target >= declared)
            {
                if (argError is not null)
                {
                    *argError = j;
                }
                return HResults.DISP_E_PARAMNOTFOUND;
            }
            if (args[target] != 0)
            {
                // ACE fails a repeated named argument with DAO 3001 (R-ACT-1 ACT-ABI); its text is not yet bound here.
                return ErrorPublication.ToExcepInfo(Failure.NotImplemented($"{Declaration.Name}.{member.Name} with a repeated named argument"), info);
            }
            args[target] = (nint)(rgvarg + j);
        }
        for (int k = 0; k < declared; k++)
        {
            bool missing = args[k] == 0 || IsMissing((NativeVariant*)args[k]);
            if (missing)
            {
                args[k] = 0;
                if (!member.Parameters[k].Optional)
                {
                    return HResults.DISP_E_PARAMNOTOPTIONAL;
                }
            }
        }
        CallResult outcome = Run(member, args);
        if (outcome.Failure is { } failure)
        {
            return ErrorPublication.ToExcepInfo(failure, info);
        }
        if (outcome.Value is { } value)
        {
            if (result is not null)
            {
                value.TransferTo(result);
            }
            value.Dispose();
        }
        return HResults.S_OK;
    }

    /// <summary>A missing optional argument: VT_ERROR DISP_E_PARAMNOTFOUND.</summary>
    private static bool IsMissing(NativeVariant* v) => v->Vt == (ushort)Foundation.VarType.Error && *(int*)&v->Int64 == HResults.DISP_E_PARAMNOTFOUND;

    private MemberDecl? Choose(int dispId, ushort flags)
    {
        foreach (InvokeKind kind in new[] { InvokeKind.Method, InvokeKind.PropertyGet, InvokeKind.PropertyPut, InvokeKind.PropertyPutRef })
        {
            if ((flags & (ushort)kind) != 0 && Declaration.Members.FirstOrDefault(m => m.MemberId == dispId && m.Kind == kind) is { } m)
            {
                return m;
            }
        }
        return null;
    }
}

/// <summary>The IUnknown and IDispatch entries every dual wrapper shares, and vtable construction from a declaration.</summary>
internal static unsafe class DualEntries
{
    /// <summary>
    /// The vtable of a dual interface: IUnknown and IDispatch, generated not-implemented entries for every declared
    /// slot, then the implemented typed entries. Built once per interface and kept for the module's lifetime.
    /// </summary>
    public static nint Build(InterfaceDecl declaration, params ReadOnlySpan<(MemberDecl Member, nint Entry)> implemented)
    {
        nint* table = (nint*)NativeMemory.AllocZeroed((nuint)(declaration.SlotCount * sizeof(nint)));
        declaration.FillUnimplemented(table);
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged<nint, uint*, int>)&GetTypeInfoCount;
        table[4] = (nint)(delegate* unmanaged<nint, uint, uint, nint*, int>)&GetTypeInfo;
        table[5] = (nint)(delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
        table[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&Invoke;
        foreach ((MemberDecl member, nint entry) in implemented)
        {
            table[member.Slot] = entry;
        }
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    public static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return HResults.E_POINTER;
        }
        return ComObject.Recover<ComObject>(self).QueryInterface(*iid, result);
    }

    [UnmanagedCallersOnly]
    public static uint AddRef(nint self) => ComObject.Recover<ComObject>(self).AddRef();

    [UnmanagedCallersOnly]
    public static uint Release(nint self) => ComObject.Recover<ComObject>(self).Release();

    /// <summary>One type information: the dispatch view of the object's interface in CalcDb's type library.</summary>
    [UnmanagedCallersOnly]
    private static int GetTypeInfoCount(nint self, uint* count)
    {
        if (count is null)
        {
            return HResults.E_POINTER;
        }
        *count = 1;
        return HResults.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetTypeInfo(nint self, uint index, uint lcid, nint* typeInfo)
    {
        if (typeInfo is null)
        {
            return HResults.E_POINTER;
        }
        *typeInfo = 0;
        if (index != 0)
        {
            return unchecked((int)0x8002000B); // DISP_E_BADINDEX
        }
        try
        {
            return TypeLibrary.TypeInfoOf(ComObject.Recover<DispatchObject>(self).Declaration, typeInfo);
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }

    [UnmanagedCallersOnly]
    private static int GetIDsOfNames(nint self, Guid* iid, char** names, uint count, uint lcid, int* ids)
    {
        try
        {
            return ComObject.Recover<DispatchObject>(self).GetIds(names, count, ids);
        }
        catch (Exception)
        {
            return HResults.E_UNEXPECTED;
        }
    }

    [UnmanagedCallersOnly]
    private static int Invoke(nint self, int dispId, Guid* iid, uint lcid, ushort flags, DispParams* parameters, NativeVariant* result, ExcepInfo* info, uint* argError)
    {
        try
        {
            return ComObject.Recover<DispatchObject>(self).Invoke(dispId, flags, parameters, result, info, argError);
        }
        catch (Exception e)
        {
            return ErrorPublication.ToExcepInfo(Failure.Defect(e), info);
        }
    }
}

/// <summary>The body of every generated not-implemented vtable entry.</summary>
internal static unsafe class Unimplemented
{
    public static int Fail(nint self, InterfaceDecl declaration, int slot, nint retval, RetvalCell cell)
    {
        if (retval != 0)
        {
            switch (cell)
            {
                case RetvalCell.Byte:
                    *(byte*)retval = 0;
                    break;
                case RetvalCell.Int16:
                    *(short*)retval = 0;
                    break;
                case RetvalCell.Int32:
                    *(int*)retval = 0;
                    break;
                case RetvalCell.Pointer:
                    *(nint*)retval = 0;
                    break;
                case RetvalCell.Variant:
                    Unsafe.InitBlock((void*)retval, 0, (uint)sizeof(NativeVariant));
                    break;
            }
        }
        string member = declaration.BySlot(slot)?.Name ?? $"slot {slot}";
        return ErrorPublication.ToErrorInfo(Failure.NotImplemented($"{declaration.Name}.{member}"), declaration.Iid);
    }
}