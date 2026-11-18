using System.Runtime.InteropServices;
using CalcDb.Com.Declarations;
using CalcDb.Foundation;
using CalcDb.Foundation.Diagnostics;
using CalcDb.Foundation.Windows;

namespace CalcDb.Com.Com;

/// <summary>
/// An operation's result: an owned value, an inline VARIANT, nothing, or a failure. Both adapters reach the same
/// operation (II-13 §3). An inline VARIANT is written to the caller's slot without an object: a scalar that owns no
/// resource (a number, a date, a Boolean, Null; task 6p9q), or a BSTR the result owns (task n7mw), which the adapter
/// moves into the slot or frees when the caller takes no result. Other values that own a resource (arrays, objects)
/// are an OwnedVariant.
/// </summary>
internal readonly record struct CallResult(OwnedVariant? Value, Failure? Failure)
{
    public NativeVariant Scalar { get; private init; }

    public bool HasScalar { get; private init; }

    /// <summary>The inline VARIANT owns its BSTR or interface reference: whoever does not move it to the caller frees it.</summary>
    public bool OwnsScalar { get; private init; }

    public static CallResult Of(OwnedVariant value) => new(value, null);

    /// <summary>An inline VT_BSTR result that takes ownership of <paramref name="bstr"/>.</summary>
    public static CallResult OfOwnedBstr(nint bstr)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Bstr;
        v.Pointer = bstr;
        return new CallResult(null, null) { Scalar = v, HasScalar = true, OwnsScalar = true };
    }

    /// <summary>An inline VT_DISPATCH result that takes ownership of one reference to <paramref name="dispatch"/> (task hdw6).</summary>
    public static CallResult OfOwnedObject(nint dispatch)
    {
        NativeVariant v = default;
        v.Vt = (ushort)VarType.Dispatch;
        v.Pointer = dispatch;
        return new CallResult(null, null) { Scalar = v, HasScalar = true, OwnsScalar = true };
    }

    /// <summary>Frees an owned inline result that was not moved to a caller.</summary>
    public unsafe void ReleaseUntaken()
    {
        if (!OwnsScalar)
        {
            return;
        }
        nint p = Scalar.Pointer;
        if ((VarType)Scalar.Vt == VarType.Bstr)
        {
            Bstrs.Free(p);
        }
        else if (p != 0)
        {
            ((delegate* unmanaged<nint, uint>)(*(nint**)p)[2])(p);
        }
    }

    /// <summary>A scalar result; <paramref name="value"/> must own no resource.</summary>
    public static CallResult OfScalar(VarType type, long payload)
    {
        NativeVariant v = default;
        v.Vt = (ushort)type;
        v.Int64 = payload;
        return new CallResult(null, null) { Scalar = v, HasScalar = true };
    }

    public static CallResult Fail(Failure failure) => new(null, failure);

    public static CallResult Nothing { get; } = new(null, null);
}

/// <summary>
/// A typed object operation. <paramref name="args"/> holds one borrowed VARIANT pointer per declared parameter, 0 when
/// an optional one was omitted. Parameters of declared scalar types arrive coerced to that type.
/// </summary>
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
/// A wrapper with a dual interface: its declaration, and the operations it implements by member. The dispatch adapter
/// binds and coerces arguments, the typed adapter (<see cref="TypedCall"/>) converts native arguments; both reach the
/// same operation. A declared member without an operation fails with the named not-implemented outcome.
/// </summary>
internal abstract unsafe class DispatchObject(InterfaceDecl declaration, nint vtable) : ComObject(vtable)
{
    public InterfaceDecl Declaration { get; } = declaration;

    protected abstract Operation? Find(MemberDecl member);

    /// <summary>Called with every failure an operation returns, before it is published (DAO records it in Errors).</summary>
    protected virtual void OnFailure(Failure failure)
    {
    }

    /// <summary>
    /// The failure an Invoke with too many arguments or a missing required one publishes, or null for the standard
    /// DISP_E_BADPARAMCOUNT and DISP_E_PARAMNOTOPTIONAL. DAO's Database and Recordset answer with their own error.
    /// </summary>
    protected virtual Failure? ArgumentCountFailure(MemberDecl member) => null;

    private int ArgumentCount(MemberDecl member, int standard, ExcepInfo* info)
    {
        if (ArgumentCountFailure(member) is not { } failure)
        {
            return standard;
        }
        OnFailure(failure);
        return ErrorPublication.ToExcepInfo(failure, info);
    }

    protected override int HeadFor(in Guid iid)
    {
        if (iid == ComIids.IDispatch)
        {
            return 0;
        }
        Guid wanted = iid;
        return DeclarationIndex.Chain(Declaration).Contains(wanted) ? 0 : -1;
    }

    public CallResult Run(MemberDecl member, ReadOnlySpan<nint> args)
    {
        CallResult result;
        if (Find(member) is not { } op)
        {
            result = CallResult.Fail(Failure.NotImplemented($"{Declaration.Name}.{member.Name}"));
        }
        else
        {
            try
            {
                result = op(this, args);
            }
            catch (Exception e)
            {
                result = CallResult.Fail(Failure.Defect(e));
            }
        }
        if (result.Failure is { } failure)
        {
            OnFailure(failure);
        }
        return result;
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
        MemberDecl[] members = Declaration.MembersNamed(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(names[0]));
        int hr = HResults.S_OK;
        ids[0] = members.Length == 0 ? -1 : members[0].MemberId;
        if (members.Length == 0)
        {
            hr = HResults.DISP_E_UNKNOWNNAME;
        }
        for (uint i = 1; i < count; i++)
        {
            ReadOnlySpan<char> p = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(names[i]);
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

    private static int IndexOf(IReadOnlyList<ParamDecl> parameters, ReadOnlySpan<char> name)
    {
        for (int k = 0; k < parameters.Count; k++)
        {
            if (name.Equals(parameters[k].Name, StringComparison.OrdinalIgnoreCase))
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
        uint count = parameters is null ? 0 : parameters->ArgCount;
        uint named = parameters is null ? 0 : parameters->NamedArgCount;
        if (named > count)
        {
            return HResults.E_INVALIDARG;
        }
        if (count > declared)
        {
            return ArgumentCount(member, HResults.DISP_E_BADPARAMCOUNT, info);
        }
        Span<nint> args = stackalloc nint[declared];
        Span<uint> positions = stackalloc uint[declared]; // each argument's index in rgvarg, for puArgErr
        NativeVariant* rgvarg = count == 0 ? null : (NativeVariant*)parameters->Args;
        int* namedIds = named == 0 ? null : (int*)parameters->NamedArgs;
        uint positional = count - named;
        for (uint i = 0; i < positional; i++)
        {
            args[(int)i] = (nint)(rgvarg + (count - 1 - i));
            positions[(int)i] = count - 1 - i;
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
            positions[target] = j;
        }
        List<OwnedVariant>? coerced = null;
        try
        {
            for (int k = 0; k < declared; k++)
            {
                if (args[k] == 0 || TypedCall.IsMissing((NativeVariant*)args[k]))
                {
                    // A required parameter that was not passed fails; one passed as Missing reaches the operation as
                    // omitted (r-cursor-1/seek-arguments: Seek "=", Missing, 20 succeeds, Seek "=" alone is 3001).
                    bool passed = args[k] != 0;
                    args[k] = 0;
                    if (!passed && !member.Parameters[k].Optional)
                    {
                        return ArgumentCount(member, HResults.DISP_E_PARAMNOTOPTIONAL, info);
                    }
                    continue;
                }
                // Declared scalar parameters are coerced as DispInvoke does; a failure names the argument.
                if (TypedCall.ScalarType(member.Parameters[k].Type) is { } vt && ((NativeVariant*)args[k])->Vt != (ushort)vt)
                {
                    OwnedVariant? c = OwnedVariant.ChangeType((NativeVariant*)args[k], vt, out int hr);
                    if (c is null)
                    {
                        if (argError is not null)
                        {
                            *argError = positions[k];
                        }
                        return hr;
                    }
                    (coerced ??= []).Add(c);
                    args[k] = (nint)c.Pointer;
                }
            }
            CallResult outcome = Run(member, args);
            if (outcome.Failure is { } failure)
            {
                return ErrorPublication.ToExcepInfo(failure, info);
            }
            if (outcome.HasScalar)
            {
                if (result is not null)
                {
                    *result = outcome.Scalar;
                    CostCounters.Current.ResultValues++;
                }
                else
                {
                    outcome.ReleaseUntaken();
                }
            }
            else if (outcome.Value is { } value)
            {
                if (result is not null)
                {
                    value.TransferTo(result);
                    CostCounters.Current.ResultValues++;
                }
                value.Dispose();
            }
            return HResults.S_OK;
        }
        finally
        {
            foreach (OwnedVariant c in coerced ?? [])
            {
                c.Dispose();
            }
        }
    }

    private static ReadOnlySpan<InvokeKind> ChoiceOrder => [InvokeKind.Method, InvokeKind.PropertyGet, InvokeKind.PropertyPut, InvokeKind.PropertyPutRef];

    /// <summary>The member a DISPID and its invoke flags select: method first, then get, put, putref (no allocation per call).</summary>
    private MemberDecl? Choose(int dispId, ushort flags)
    {
        IReadOnlyList<MemberDecl> members = Declaration.Members;
        foreach (InvokeKind kind in ChoiceOrder)
        {
            if ((flags & (ushort)kind) == 0)
            {
                continue;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].MemberId == dispId && members[i].Kind == kind)
                {
                    return members[i];
                }
            }
        }
        return null;
    }
}

/// <summary>The IUnknown and IDispatch entries every dual wrapper shares, and vtable construction from a declaration.</summary>
internal static unsafe class DualEntries
{
    /// <summary>
    /// The vtable of a dual interface: IUnknown and IDispatch, then the generated typed entry of every declared slot,
    /// each reaching its operation through <see cref="TypedCall"/>. Built once per interface, kept for the module's lifetime.
    /// </summary>
    public static nint Build(InterfaceDecl declaration)
    {
        nint* table = (nint*)NativeMemory.AllocZeroed((nuint)(declaration.SlotCount * sizeof(nint)));
        declaration.FillEntries(table);
        table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        table[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        table[3] = (nint)(delegate* unmanaged<nint, uint*, int>)&GetTypeInfoCount;
        table[4] = (nint)(delegate* unmanaged<nint, uint, uint, nint*, int>)&GetTypeInfo;
        table[5] = (nint)(delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
        table[6] = (nint)(delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)&Invoke;
        return (nint)table;
    }

    [UnmanagedCallersOnly]
    public static int QueryInterface(nint self, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return HResults.E_POINTER;
        }
        CostCounters.Current.QueryInterfaces++;
        return ComObject.Recover<ComObject>(self).QueryInterface(*iid, result);
    }

    [UnmanagedCallersOnly]
    public static uint AddRef(nint self)
    {
        CostCounters.Current.AddRefs++;
        return ComObject.Recover<ComObject>(self).AddRef();
    }

    [UnmanagedCallersOnly]
    public static uint Release(nint self)
    {
        CostCounters.Current.Releases++;
        return ComObject.Recover<ComObject>(self).Release();
    }

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
        CostCounters.Current.DispatchNamesResolved += count;
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
        CostCounters.Current.DispatchInvokes++;
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

/// <summary>
/// The typed (vtable) adapter: every generated slot entry passes its raw arguments here. Each argument is turned into a
/// borrowed VARIANT by its declared type, the operation runs, and the result is written to the retval cell by the
/// declared return type. The retval cell is cleared first, so a failure leaves a null output (II-13 §3).
/// </summary>
internal static unsafe class TypedCall
{
    public static int Run(nint self, InterfaceDecl declaration, int slot, ulong* raw, int count, nint retval)
    {
        CostCounters.Current.VtableEntries++;
        MemberDecl? member = declaration.BySlot(slot);
        if (member is not null && retval != 0)
        {
            Clear(member.ReturnType, retval);
        }
        try
        {
            if (member is null)
            {
                return ErrorPublication.ToErrorInfo(Failure.NotImplemented($"{declaration.Name} slot {slot}"), declaration.Iid);
            }
            DispatchObject target = ComObject.Recover<DispatchObject>(self);
            NativeVariant* cells = stackalloc NativeVariant[Math.Max(count, 1)];
            Span<nint> args = stackalloc nint[member.Parameters.Count];
            for (int k = 0; k < count && k < args.Length; k++)
            {
                cells[k] = default;
                string type = member.Parameters[k].Type;
                if (type == "VT_VARIANT")
                {
                    args[k] = IsMissing((NativeVariant*)raw[k]) ? 0 : (nint)raw[k];
                    continue;
                }
                if (!Box(type, raw[k], cells + k))
                {
                    return ErrorPublication.ToErrorInfo(Failure.NotImplemented($"{declaration.Name}.{member.Name} with a {type} argument"), declaration.Iid);
                }
                args[k] = (nint)(cells + k);
            }
            CallResult outcome = target.Run(member, args);
            if (outcome.Failure is { } failure)
            {
                return ErrorPublication.ToErrorInfo(failure, declaration.Iid);
            }
            if (outcome.HasScalar)
            {
                if (retval != 0)
                {
                    StoreScalar(member.ReturnType, outcome, retval);
                    CostCounters.Current.ResultValues++;
                }
                else
                {
                    outcome.ReleaseUntaken();
                }
            }
            else if (outcome.Value is { } value)
            {
                using (value)
                {
                    if (retval != 0)
                    {
                        Store(member.ReturnType, value, retval);
                        CostCounters.Current.ResultValues++;
                    }
                }
            }
            return HResults.S_OK;
        }
        catch (Exception e)
        {
            return ErrorPublication.ToErrorInfo(Failure.Defect(e), declaration.Iid);
        }
    }

    /// <summary>A missing optional argument: VT_ERROR DISP_E_PARAMNOTFOUND.</summary>
    public static bool IsMissing(NativeVariant* v) => v->Vt == (ushort)VarType.Error && *(int*)&v->Int64 == HResults.DISP_E_PARAMNOTFOUND;

    /// <summary>The VARTYPE of a declared scalar parameter type, or null for VARIANT, object and pointer types.</summary>
    public static VarType? ScalarType(string type) => type switch
    {
        "VT_BSTR" => VarType.Bstr,
        "VT_I2" => VarType.I2,
        "VT_I4" => VarType.I4,
        "VT_BOOL" => VarType.Bool,
        "VT_R4" => VarType.R4,
        "VT_R8" => VarType.R8,
        "VT_DATE" => VarType.Date,
        "VT_CY" => VarType.Currency,
        "VT_UI1" => VarType.UI1,
        _ => null,
    };

    private static bool Box(string type, ulong raw, NativeVariant* cell)
    {
        if (ScalarType(type) is { } vt)
        {
            cell->Vt = (ushort)vt;
            cell->Int64 = vt switch
            {
                VarType.I2 or VarType.Bool => (short)raw,
                VarType.I4 => (int)raw,
                VarType.UI1 => (byte)raw,
                VarType.R4 => (uint)raw, // float bits
                _ => (long)raw, // BSTR pointer, R8/DATE bits, CY
            };
            return true;
        }
        if (type.StartsWith("PTR USERDEFINED ", StringComparison.Ordinal) || type is "VT_DISPATCH" or "VT_UNKNOWN")
        {
            cell->Vt = (ushort)(type == "VT_UNKNOWN" ? VarType.Unknown : VarType.Dispatch);
            cell->Pointer = (nint)raw;
            return true;
        }
        return false;
    }

    private static void Clear(string returnType, nint retval)
    {
        switch (returnType)
        {
            case "VT_VOID":
                break;
            case "VT_VARIANT":
                *(NativeVariant*)retval = default;
                break;
            case "VT_I2" or "VT_BOOL" or "VT_UI2":
                *(short*)retval = 0;
                break;
            case "VT_UI1":
                *(byte*)retval = 0;
                break;
            case "VT_I4" or "VT_R4" or "VT_UI4" or "VT_INT" or "VT_UINT":
                *(int*)retval = 0;
                break;
            default:
                *(long*)retval = 0; // pointers, BSTR, 8-byte scalars
                break;
        }
    }

    /// <summary>
    /// An inline VARIANT written by the declared return type: a scalar is copied, an owned BSTR moves to the caller as a
    /// VARIANT or a BSTR (and is freed under any other declared type, which is then a defect).
    /// </summary>
    private static void StoreScalar(string returnType, in CallResult outcome, nint retval)
    {
        NativeVariant v = outcome.Scalar;
        if (outcome.OwnsScalar && returnType == "VT_BSTR" && (VarType)v.Vt == VarType.Bstr)
        {
            *(nint*)retval = v.Pointer;
            return;
        }
        if (returnType == "VT_VARIANT")
        {
            *(NativeVariant*)retval = v;
            return;
        }
        if (outcome.OwnsScalar && (VarType)v.Vt == VarType.Dispatch && returnType.StartsWith("PTR USERDEFINED ", StringComparison.Ordinal))
        {
            // The declared interface is queried from the owned reference, which is then released.
            StoreObject(returnType, v.Pointer, retval);
            outcome.ReleaseUntaken();
            return;
        }
        if (outcome.OwnsScalar)
        {
            outcome.ReleaseUntaken();
            throw new InvalidOperationException($"no typed return rule for an owned {(VarType)v.Vt} as {returnType}");
        }
        switch (returnType)
        {
            case "VT_I2" or "VT_BOOL":
                *(short*)retval = (short)v.Int64;
                return;
            case "VT_UI1":
                *(byte*)retval = (byte)v.Int64;
                return;
            case "VT_I4" or "VT_R4":
                *(int*)retval = (int)v.Int64;
                return;
            case "VT_R8" or "VT_DATE" or "VT_CY":
                *(long*)retval = v.Int64;
                return;
        }
        throw new InvalidOperationException($"no typed return rule for a scalar as {returnType}");
    }

    private static void Store(string returnType, OwnedVariant value, nint retval)
    {
        NativeVariant* v = value.Pointer;
        switch (returnType)
        {
            case "VT_VARIANT":
                value.TransferTo((NativeVariant*)retval);
                return;
            case "VT_BSTR":
                *(nint*)retval = value.TransferBstr();
                return;
            case "VT_I2" or "VT_BOOL":
                *(short*)retval = (short)v->Int64;
                return;
            case "VT_UI1":
                *(byte*)retval = (byte)v->Int64;
                return;
            case "VT_I4" or "VT_R4":
                *(int*)retval = (int)v->Int64;
                return;
            case "VT_R8" or "VT_DATE" or "VT_CY":
                *(long*)retval = v->Int64;
                return;
        }
        if (returnType.StartsWith("PTR USERDEFINED ", StringComparison.Ordinal))
        {
            StoreObject(returnType, v->Pointer, retval);
            return;
        }
        throw new InvalidOperationException($"no typed return rule for {returnType}");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, InterfaceDecl?> DeclaredReturns = new(StringComparer.Ordinal);

    /// <summary>The declared interface of a returned object (a coclass name stands for its default interface), queried from <paramref name="unknown"/>.</summary>
    private static void StoreObject(string returnType, nint unknown, nint retval)
    {
        // Resolved once per declared return type, not per call (task hdw6).
        InterfaceDecl? declared = DeclaredReturns.GetOrAdd(returnType, static t => DeclarationIndex.Interface(t[16..]));
        nint result = 0;
        Guid iid = declared?.Iid ?? ComIids.IDispatch;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)(*(nint**)unknown)[0])(unknown, &iid, &result);
        *(nint*)retval = hr >= 0 ? result : 0;
    }
}