namespace CalcDb.Perf;

/// <summary>A failed member call inside a measured body: the sample keeps it as its outcome.</summary>
internal sealed class MeasuredCallException(string member, int hr) : Exception($"{member} failed with {Native.Hr(hr)}")
{
    public int HResult2 { get; } = hr;
}

/// <summary>
/// The member calls a loop body makes, on one surface (II-15 §26). Implemented by structs so each body is compiled
/// once per surface with direct calls.
/// </summary>
internal unsafe interface ILane
{
    bool Eof();

    void MoveNext();

    nint Fields();

    nint Item(nint fields, Variant* index);

    void Value(nint field, Variant* result);
}

/// <summary>
/// The declared dual interfaces, called through their vtables as early-bound VBA and native clients call them. The
/// byte offsets are those of the captured DAO type library (Recordset 00000031: EOF 104, MoveNext 472, Fields 360;
/// Fields 00000053: Item 96; _Field 00000051: Value 136), the same declarations CalcDb.Com is generated from. Each
/// entry is read from the object's own vtable at the call.
/// </summary>
internal unsafe struct VtableLane(nint recordset) : ILane
{
    public static readonly Guid IidRecordset = new("00000031-0000-0010-8000-00AA006D2EA4");
    public static readonly Guid IidField = new("00000051-0000-0010-8000-00AA006D2EA4");
    public static readonly Guid IidFields = new("00000053-0000-0010-8000-00AA006D2EA4");

    private static nint Entry(nint obj, int byteOffset) => (*(nint**)obj)[byteOffset / 8];

    public bool Eof()
    {
        short eof;
        int hr = ((delegate* unmanaged<nint, short*, int>)Entry(recordset, 104))(recordset, &eof);
        return hr < 0 ? throw new MeasuredCallException("EOF", hr) : eof != 0;
    }

    public void MoveNext()
    {
        int hr = ((delegate* unmanaged<nint, int>)Entry(recordset, 472))(recordset);
        if (hr < 0)
        {
            throw new MeasuredCallException("MoveNext", hr);
        }
    }

    public nint Fields()
    {
        nint fields;
        int hr = ((delegate* unmanaged<nint, nint*, int>)Entry(recordset, 360))(recordset, &fields);
        return hr < 0 ? throw new MeasuredCallException("Fields", hr) : fields;
    }

    public nint Item(nint fields, Variant* index)
    {
        nint field;
        int hr = ((delegate* unmanaged<nint, Variant, nint*, int>)Entry(fields, 96))(fields, *index, &field);
        return hr < 0 ? throw new MeasuredCallException("Item", hr) : field;
    }

    public void Value(nint field, Variant* result)
    {
        int hr = ((delegate* unmanaged<nint, Variant*, int>)Entry(field, 136))(field, result);
        if (hr < 0)
        {
            throw new MeasuredCallException("Value", hr);
        }
    }
}

/// <summary>IDispatch::Invoke with the DISPIDs resolved once at setup (GetIDsOfNames is outside the loop).</summary>
internal unsafe struct DispatchLane : ILane
{
    private readonly nint _recordset;
    private readonly int _eof, _moveNext, _fields;

    public DispatchLane(nint recordset)
    {
        _recordset = recordset;
        Check(Native.GetId(recordset, "EOF", out _eof), "GetIDsOfNames EOF");
        Check(Native.GetId(recordset, "MoveNext", out _moveNext), "GetIDsOfNames MoveNext");
        Check(Native.GetId(recordset, "Fields", out _fields), "GetIDsOfNames Fields");
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new MeasuredCallException(what, hr);
        }
    }

    public bool Eof()
    {
        Variant r = default;
        Check(Native.Invoke(_recordset, _eof, Native.DispatchPropertyGet, [], &r), "EOF");
        return (short)r.Int64 != 0;
    }

    public void MoveNext()
    {
        Variant r = default;
        Check(Native.Invoke(_recordset, _moveNext, Native.DispatchMethod, [], &r), "MoveNext");
        _ = Native.VariantClear(&r);
    }

    public nint Fields()
    {
        Variant r = default;
        Check(Native.Invoke(_recordset, _fields, Native.DispatchPropertyGet, [], &r), "Fields");
        return r.Pointer;
    }

    public nint Item(nint fields, Variant* index)
    {
        Variant r = default;
        Check(Native.Invoke(fields, 0, Native.DispatchMethod | Native.DispatchPropertyGet, [*index], &r), "Item");
        return r.Pointer;
    }

    public void Value(nint field, Variant* result) => Check(Native.Invoke(field, 0, Native.DispatchPropertyGet, [], result), "Value");
}

/// <summary>What one timed pass consumed: rows, sink, text units and the client's own exact call counts.</summary>
internal readonly record struct PassResult(long Rows, long Sink, long TextUnits, long Entries, long Releases);

/// <summary>
/// The loop bodies of II-15 §26, from one template: only the value statement differs. The reused slot <c>v</c> is
/// cleared before each get, as an assignment to a Variant variable releases its old value. The consumer is fixed per case.
/// </summary>
internal static unsafe class Bodies
{
    public static PassResult Run<TLane>(ref TLane lane, PerformanceCase c, nint cachedField, nint cachedFields, Variant* index)
        where TLane : struct, ILane
    {
        Variant v = default;
        long rows = 0, sink = 0, units = 0, entries = 0, releases = 0;
        bool clng = c.Consumer == "xor-clng";
        try
        {
            while (true)
            {
                entries++;
                if (lane.Eof() || rows >= c.MaxRows)
                {
                    break;
                }
                switch (c.Body)
                {
                    case "cached-field":
                        _ = Native.VariantClear(&v);
                        lane.Value(cachedField, &v);
                        entries++;
                        Consume(&v, clng, ref sink, ref units);
                        break;
                    case "repeated":
                        for (int k = 0; k < c.Repeat; k++)
                        {
                            _ = Native.VariantClear(&v);
                            lane.Value(cachedField, &v);
                            entries++;
                            Consume(&v, clng, ref sink, ref units);
                        }
                        break;
                    case "cached-fields":
                    {
                        nint field = lane.Item(cachedFields, index);
                        _ = Native.VariantClear(&v);
                        lane.Value(field, &v);
                        entries += 2;
                        _ = Native.Release(field);
                        releases++;
                        Consume(&v, clng, ref sink, ref units);
                        break;
                    }
                    default: // named, ordinal: rs.Fields(index).Value
                    {
                        nint fields = lane.Fields();
                        nint field = lane.Item(fields, index);
                        _ = Native.VariantClear(&v);
                        lane.Value(field, &v);
                        entries += 3;
                        _ = Native.Release(field);
                        _ = Native.Release(fields);
                        releases += 2;
                        Consume(&v, clng, ref sink, ref units);
                        break;
                    }
                }
                rows++;
                lane.MoveNext();
                entries++;
            }
        }
        finally
        {
            _ = Native.VariantClear(&v);
        }
        return new PassResult(rows, sink, units, entries, releases);
    }

    private static void Consume(Variant* v, bool clng, ref long sink, ref long units)
    {
        if (clng)
        {
            Variant i4 = default;
            int hr = Native.VariantChangeType(&i4, v, 0, Native.VtI4);
            if (hr < 0)
            {
                throw new MeasuredCallException("CLng", hr);
            }
            sink ^= (int)i4.Int64;
            return;
        }
        if (v->Vt != Native.VtBstr)
        {
            throw new MeasuredCallException($"xor-length of VARTYPE {v->Vt}", unchecked((int)0x80020005));
        }
        uint length = Native.SysStringLen(v->Pointer);
        sink ^= length;
        units += length;
    }
}
