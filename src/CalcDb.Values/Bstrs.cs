using System.Buffers;

using CalcDb.Foundation;

namespace CalcDb.Values;

/// <summary>BSTR construction through the Automation allocator (II-02 §4, T-INT-35/36).</summary>
public static unsafe class Bstrs
{
    /// <summary>
    /// A new BSTR of <paramref name="units"/> UTF-16 units that <paramref name="fill"/> writes straight into its own
    /// buffer (D-STRING-1 direct construction, task n7mw): no intermediate managed string. The caller owns the
    /// result. A fill that throws frees the string first. Zero units give an allocated empty BSTR, not a null one.
    /// </summary>
    public static nint FromUnits<TState>(int units, TState state, SpanAction<char, TState> fill)
        where TState : allows ref struct
    {
        // A null source allocates an uninitialized string of this length; the allocator writes the terminator. Zero
        // units pass a non-null source, as OwnedVariant.FromBstrBytes does.
        byte one = 0;
        nint bstr = OleAut.SysAllocStringByteLen(units == 0 ? &one : null, checked((uint)units * 2));
        if (bstr == 0)
        {
            throw new AutomationServiceException("SysAllocStringByteLen", OleAut.E_OUTOFMEMORY);
        }
        try
        {
            fill(new Span<char>((void*)bstr, units), state);
        }
        catch
        {
            OleAut.SysFreeString(bstr);
            throw;
        }
        Foundation.Diagnostics.CostCounters counters = Foundation.Diagnostics.CostCounters.Current;
        counters.BstrAllocations++;
        counters.BstrPayloadBytes += units * 2;
        return bstr;
    }

    /// <summary>The UTF-16 units of a BSTR, borrowed for as long as the caller's string lives (empty for a null BSTR).</summary>
    public static ReadOnlySpan<char> Chars(nint bstr) => bstr == 0 ? default : new ReadOnlySpan<char>((void*)bstr, (int)OleAut.SysStringLen(bstr));

    /// <summary>The raw bytes of a BSTR (including odd byte lengths from StrConv(..., 128)), borrowed while the BSTR lives.</summary>
    public static ReadOnlySpan<byte> Bytes(nint bstr) => bstr == 0 ? default : new ReadOnlySpan<byte>((void*)bstr, (int)OleAut.SysStringByteLen(bstr));

    /// <summary>Frees a BSTR this caller owns (SysFreeString; a null BSTR is allowed).</summary>
    public static void Free(nint bstr) => OleAut.SysFreeString(bstr);
}
