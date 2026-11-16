using System.Runtime.InteropServices;
using CalcDb.Evidence;

namespace CalcDb.Oracle.Worker;

/// <summary>
/// Raw IDispatch driver for the <c>native-idispatch/x64</c> caller surface (R-ORACLE-1 OR-WIRE/OR-OBS).
/// Calls go through vtable slots with unmanaged VARIANTs, so the capture sees exactly what the native
/// object returned: no RCW, no managed VARIANT conversion, explicit Release of every owned reference.
/// </summary>
internal sealed unsafe class Dispatch
{
    private const int DispEException = unchecked((int)0x80020009);
    private readonly Dictionary<nint, string> _identities = [];
    private int _nextLabel;
    private readonly int _lcid;

    public Dispatch(int lcid)
    {
        _lcid = lcid;
    }

    public int GetIdsOfNames(nint dispatch, string name, out int dispId)
    {
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, char**, uint, int, int*, int>)(*(nint**)dispatch)[5];
        Guid iidNull = Guid.Empty;
        int id = 0;
        int hr;
        fixed (char* pName = name)
        {
            char* names = pName;
            hr = fn(dispatch, &iidNull, &names, 1, _lcid, &id);
        }
        dispId = id;
        return hr;
    }

    /// <summary>The outcome of one Invoke and its observed error channels.</summary>
    public sealed record Outcome(
        int Hr,
        CapturedValue? Result,
        nint ResultObject,
        uint ArgErr,
        ExcepInfoObservation? ExcepInfo,
        ErrorInfoObservation? ErrorInfo,
        IReadOnlyList<string> Order);

    /// <summary>
    /// Invoke a member. <paramref name="args"/> are given in source order and passed reversed, as
    /// IDispatch requires. A returned object is AddRef'ed by the VARIANT and handed to the caller in
    /// <see cref="Outcome.ResultObject"/>; every other result is captured and cleared here.
    /// </summary>
    public Outcome Invoke(nint dispatch, int dispId, ushort flags, Variant[] args, IReadOnlyList<string> recipe)
    {
        var order = new List<string>();
        if (recipe.Contains("clearErrorInfoBefore"))
        {
            _ = Native.SetErrorInfo(0, 0);
            order.Add("clearErrorInfoBefore");
        }

        var reversed = new Variant[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            reversed[i] = args[args.Length - 1 - i];
        }

        Variant result = default;
        ExcepInfo excep = default;
        uint argErr = 0;
        int hr;
        var fn = (delegate* unmanaged[Stdcall]<nint, int, Guid*, int, ushort, DispParams*, Variant*, ExcepInfo*, uint*, int>)(*(nint**)dispatch)[6];
        // A property put passes its value (the last source argument, first after reversal) as the named
        // argument DISPID_PROPERTYPUT.
        int putId = -3;
        bool isPut = (flags & (4 | 8)) != 0 && args.Length > 0;
        fixed (Variant* pArgs = reversed)
        {
            var dp = new DispParams { Args = args.Length == 0 ? null : pArgs, NamedArgs = isPut ? &putId : null, ArgCount = (uint)args.Length, NamedArgCount = isPut ? 1u : 0u };
            Guid iidNull = Guid.Empty;
            hr = fn(dispatch, dispId, &iidNull, _lcid, flags, &dp, &result, &excep, &argErr);
        }
        order.Add("return");

        CapturedValue? captured = null;
        nint resultObject = 0;
        if (hr >= 0)
        {
            captured = CaptureValue(&result);
            int vt = result.Vt;
            if ((vt == 9 || vt == 13) && result.Ptr != 0)
            {
                resultObject = result.Ptr; // ownership moves to the caller; do not clear
            }
            else
            {
                _ = Native.VariantClear(&result);
            }
        }

        ExcepInfoObservation? excepInfo = null;
        if (hr == DispEException && recipe.Contains("excepinfo"))
        {
            excepInfo = CaptureExcepInfo(ref excep, recipe.Contains("deferredFill"));
            order.Add("excepinfo");
        }
        else
        {
            FreeExcepInfo(ref excep);
        }

        ErrorInfoObservation? errorInfo = null;
        if (hr < 0 && recipe.Contains("errorInfo"))
        {
            errorInfo = CaptureErrorInfo();
            order.Add("errorInfo");
        }

        return new Outcome(hr, captured, resultObject, argErr, excepInfo, errorInfo, order);
    }

    /// <summary>Capture the active value of a VARIANT without changing or freeing it.</summary>
    public CapturedValue CaptureValue(Variant* v)
    {
        int vt = v->Vt;
        if ((vt & 0x4000) != 0)
        {
            return CapturedValue.Refused(vt, "VT_BYREF results are not representable in calcdb.capture/1.");
        }
        if ((vt & 0x2000) != 0)
        {
            return CaptureArray(vt, v->Ptr);
        }
        return vt switch
        {
            0 or 1 => CapturedValue.Simple(vt),
            2 => CapturedValue.FromInteger(vt, v->I2),
            3 or 22 => CapturedValue.FromInteger(vt, v->I4),
            16 => CapturedValue.FromInteger(vt, v->I1),
            17 => CapturedValue.FromInteger(vt, v->UI1),
            18 => CapturedValue.FromInteger(vt, (ushort)v->I2),
            19 or 23 => CapturedValue.FromInteger(vt, (uint)v->I4),
            20 => CapturedValue.FromInteger(vt, v->I8),
            21 => CapturedValue.FromUnsigned(vt, (ulong)v->I8),
            11 => CapturedValue.FromInteger(vt, v->I2),
            10 => CapturedValue.FromInteger(vt, v->I4),
            4 => CapturedValue.FromSingle(v->R4),
            5 or 7 => CapturedValue.FromDouble(vt, v->R8),
            6 => CapturedValue.FromCurrency(v->I8),
            8 => CaptureBstr(v->Ptr),
            14 => new CapturedValue { Vt = vt, VtName = CapturedValue.NameOf(vt), Decimal = new DecimalParts(v->DecScale, v->DecSign, v->DecHi32, v->DecLo64) },
            9 or 13 => CaptureObject(vt, v->Ptr),
            _ => CapturedValue.Refused(vt, "VARTYPE not representable in calcdb.capture/1."),
        };
    }

    public static CapturedValue CaptureBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return CapturedValue.FromBstrBytes([]) with { Display = "null BSTR pointer" };
        }
        uint length = Native.SysStringByteLen(bstr);
        return CapturedValue.FromBstrBytes(new ReadOnlySpan<byte>((void*)bstr, checked((int)length)));
    }

    /// <summary>
    /// Release a reference this worker owns. When the count reaches zero the object is gone and its canonical
    /// pointer may be reused by a new object, so its identity label is retired.
    /// </summary>
    public uint ReleaseOwned(nint unknown)
    {
        nint canonical = 0;
        if (Native.QueryInterface(unknown, Native.IidIUnknown, out nint c) >= 0 && c != 0)
        {
            canonical = c;
            Native.Release(c);
        }
        uint count = Native.Release(unknown);
        if (count == 0 && canonical != 0)
        {
            _identities.Remove(canonical);
        }
        return count;
    }

    /// <summary>
    /// Canonical IUnknown identity label (II-15 §5). A label is retired when the worker sees its object
    /// destroyed, so a later object at the same address gets a new label; labels never repeat in a capture.
    /// </summary>
    public string IdentityOf(nint unknown)
    {
        if (Native.QueryInterface(unknown, Native.IidIUnknown, out nint canonical) < 0 || canonical == 0)
        {
            return "unknown-identity";
        }
        Native.Release(canonical);
        if (!_identities.TryGetValue(canonical, out string? label))
        {
            label = "u" + (++_nextLabel).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _identities[canonical] = label;
        }
        return label;
    }

    private CapturedValue CaptureObject(int vt, nint ptr)
    {
        if (ptr == 0)
        {
            return new CapturedValue { Vt = vt, VtName = CapturedValue.NameOf(vt), Display = "null interface pointer" };
        }
        string identity = IdentityOf(ptr);
        (string? name, string? iid) = vt == 9 ? TypeOf(ptr) : (null, null);
        return new CapturedValue { Vt = vt, VtName = CapturedValue.NameOf(vt), Identity = identity, TypeName = name, TypeIid = iid };
    }

    /// <summary>The object's ITypeInfo name and GUID: IDispatch::GetTypeInfo, GetTypeAttr, GetDocumentation.</summary>
    public (string? Name, string? Iid) TypeOf(nint dispatch)
    {
        var getTypeInfo = (delegate* unmanaged[Stdcall]<nint, uint, int, nint*, int>)(*(nint**)dispatch)[4];
        nint typeInfo = 0;
        if (getTypeInfo(dispatch, 0, _lcid, &typeInfo) < 0 || typeInfo == 0)
        {
            return (null, null);
        }
        try
        {
            nint* vtbl = *(nint**)typeInfo;
            string? iid = null;
            nint attr = 0;
            var getTypeAttr = (delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[3];
            if (getTypeAttr(typeInfo, &attr) >= 0 && attr != 0)
            {
                iid = (*(Guid*)attr).ToString("B").ToUpperInvariant();
                ((delegate* unmanaged[Stdcall]<nint, nint, void>)vtbl[19])(typeInfo, attr);
            }
            nint name = 0;
            var getDoc = (delegate* unmanaged[Stdcall]<nint, int, nint*, nint*, uint*, nint*, int>)vtbl[12];
            string? text = null;
            if (getDoc(typeInfo, -1, &name, null, null, null) >= 0 && name != 0)
            {
                text = Marshal.PtrToStringBSTR(name);
                Native.SysFreeString(name);
            }
            return (text, iid);
        }
        finally
        {
            Native.Release(typeInfo);
        }
    }

    private static CapturedValue CaptureArray(int vt, nint psa)
    {
        if (psa == 0)
        {
            return new CapturedValue { Vt = vt, VtName = CapturedValue.NameOf(vt), Display = "null SAFEARRAY" };
        }
        uint dims = Native.SafeArrayGetDim(psa);
        var bounds = new List<ArrayBound>();
        for (uint d = 1; d <= dims; d++)
        {
            Native.SafeArrayGetLBound(psa, d, out int lb);
            Native.SafeArrayGetUBound(psa, d, out int ub);
            bounds.Add(new ArrayBound(lb, (long)ub - lb + 1));
        }
        Native.SafeArrayGetVartype(psa, out ushort elementVt);
        if (elementVt == 17 && dims == 1)
        {
            long count = bounds[0].Count;
            if (Native.SafeArrayAccessData(psa, out nint data) >= 0)
            {
                string hex = Convert.ToHexStringLower(new ReadOnlySpan<byte>((void*)data, checked((int)count)));
                _ = Native.SafeArrayUnaccessData(psa);
                return new CapturedValue { Vt = vt, VtName = CapturedValue.NameOf(vt), Array = new ArrayValue(elementVt, bounds) { Bytes = hex } };
            }
        }
        return CapturedValue.Refused(vt, $"SAFEARRAY of VARTYPE {elementVt} with {dims} dimension(s) is not yet captured element-wise.") with
        {
            Array = new ArrayValue(elementVt, bounds),
        };
    }

    private static ExcepInfoObservation CaptureExcepInfo(ref ExcepInfo excep, bool allowDeferredFill)
    {
        bool deferred = excep.DeferredFillIn != 0;
        string? deferredHr = null;
        if (deferred && allowDeferredFill)
        {
            fixed (ExcepInfo* p = &excep)
            {
                var fill = (delegate* unmanaged[Stdcall]<ExcepInfo*, int>)excep.DeferredFillIn;
                deferredHr = Native.Hr(fill(p));
            }
        }
        var observation = new ExcepInfoObservation
        {
            WCode = excep.WCode,
            Scode = Native.Hr(excep.Scode),
            Source = excep.BstrSource == 0 ? null : CaptureBstr(excep.BstrSource),
            Description = excep.BstrDescription == 0 ? null : CaptureBstr(excep.BstrDescription),
            HelpFile = excep.BstrHelpFile == 0 ? null : CaptureBstr(excep.BstrHelpFile),
            HelpContext = excep.HelpContext,
            DeferredFillPresent = deferred,
            DeferredFillHresult = deferredHr,
        };
        FreeExcepInfo(ref excep);
        return observation;
    }

    private static void FreeExcepInfo(ref ExcepInfo excep)
    {
        Native.SysFreeString(excep.BstrSource);
        Native.SysFreeString(excep.BstrDescription);
        Native.SysFreeString(excep.BstrHelpFile);
        excep.BstrSource = excep.BstrDescription = excep.BstrHelpFile = 0;
    }

    private static ErrorInfoObservation CaptureErrorInfo()
    {
        int hr = Native.GetErrorInfo(0, out nint info);
        if (hr != 0 || info == 0)
        {
            return new ErrorInfoObservation { GetErrorInfoHresult = Native.Hr(hr), Present = false };
        }
        try
        {
            nint* vtbl = *(nint**)info;
            var getters = new Dictionary<string, string>();
            Guid guid;
            int ghr = ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)vtbl[3])(info, &guid);
            getters["GetGUID"] = Native.Hr(ghr);
            CapturedValue? Bstr(int slot, string getter)
            {
                nint b = 0;
                int r = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)vtbl[slot])(info, &b);
                getters[getter] = Native.Hr(r);
                if (r < 0 || b == 0)
                {
                    return null;
                }
                CapturedValue v = CaptureBstr(b);
                Native.SysFreeString(b);
                return v;
            }
            CapturedValue? source = Bstr(4, "GetSource");
            CapturedValue? description = Bstr(5, "GetDescription");
            CapturedValue? helpFile = Bstr(6, "GetHelpFile");
            uint context;
            int chr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)vtbl[7])(info, &context);
            getters["GetHelpContext"] = Native.Hr(chr);
            return new ErrorInfoObservation
            {
                GetErrorInfoHresult = Native.Hr(hr),
                Present = true,
                Guid = ghr >= 0 ? guid.ToString("B").ToUpperInvariant() : null,
                Source = source,
                Description = description,
                HelpFile = helpFile,
                HelpContext = chr >= 0 ? context : null,
                GetterHresults = getters,
            };
        }
        finally
        {
            Native.Release(info);
        }
    }
}
