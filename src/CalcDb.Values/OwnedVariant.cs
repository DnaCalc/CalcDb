using System.Runtime.InteropServices;

using CalcDb.Foundation;

namespace CalcDb.Values;

/// <summary>The ownership states of II-02 §3 that an owned canonical value can be in.</summary>
public enum OwnershipState
{
    /// <summary>An initialized VT_EMPTY destination with no owned payload.</summary>
    Empty,

    /// <summary>An owned scalar or an owned BSTR/array/object resource.</summary>
    Owned,

    /// <summary>A clear failed (for example on a locked array): the resource is still owned and must still be settled.</summary>
    CleanupPending,

    /// <summary>Its payload was moved to another owner; it is empty and releases nothing.</summary>
    MovedFrom,
}

/// <summary>
/// One owned canonical VARIANT (W-02/native-declarations-values; II-02 §§3–4, K-VALUE). The 24 bytes live in native
/// memory with a stable address, so they can be passed to COM as they are. Construction always goes through a fresh
/// initialized temporary: no resource pointer is installed before its cleanup state exists, and nothing partial is
/// published on failure. Owner, generation and budget never live inside the bytes.
/// </summary>
public sealed unsafe class OwnedVariant : IDisposable
{
    private NativeVariant* _cell;

    private OwnedVariant()
    {
        _cell = (NativeVariant*)NativeMemory.AlignedAlloc((nuint)sizeof(NativeVariant), 8);
        OleAut.VariantInit(_cell);
        State = OwnershipState.Empty;
    }

    public OwnershipState State { get; private set; }

    /// <summary>The HRESULT of the clear that left this value in <see cref="OwnershipState.CleanupPending"/>.</summary>
    public int? CleanupFailure { get; private set; }

    /// <summary>The canonical bytes, for a call that borrows them for its interval.</summary>
    public NativeVariant* Pointer => _cell is null ? throw new ObjectDisposedException(nameof(OwnedVariant)) : _cell;

    public VarType Type => (VarType)Pointer->Vt;

    public static OwnedVariant Empty() => new();

    public static OwnedVariant Null() => Scalar(VarType.Null, v => { });

    public static OwnedVariant FromInt32(int value) => Scalar(VarType.I4, v => *(int*)&v->Int64 = value);

    public static OwnedVariant FromInt16(short value) => Scalar(VarType.I2, v => *(short*)&v->Int64 = value);

    public static OwnedVariant FromByte(byte value) => Scalar(VarType.UI1, v => *(byte*)&v->Int64 = value);

    public static OwnedVariant FromInt64(long value) => Scalar(VarType.I8, v => v->Int64 = value);

    public static OwnedVariant FromDouble(double value) => Scalar(VarType.R8, v => v->Double = value);

    public static OwnedVariant FromSingle(float value) => Scalar(VarType.R4, v => *(float*)&v->Int64 = value);

    /// <summary>VT_BOOL: VARIANT_TRUE is -1, VARIANT_FALSE is 0.</summary>
    public static OwnedVariant FromBoolean(bool value) => Scalar(VarType.Bool, v => *(short*)&v->Int64 = value ? (short)-1 : (short)0);

    /// <summary>VT_CY from its scaled 64-bit integer (amount × 10,000).</summary>
    public static OwnedVariant FromCurrency(long scaled) => Scalar(VarType.Currency, v => v->Int64 = scaled);

    /// <summary>VT_DATE from its OLE Automation date bits.</summary>
    public static OwnedVariant FromDate(double oaDate) => Scalar(VarType.Date, v => v->Double = oaDate);

    public static OwnedVariant FromError(int scode) => Scalar(VarType.Error, v => *(int*)&v->Int64 = scode);

    /// <summary>The "missing" optional argument: VT_ERROR DISP_E_PARAMNOTFOUND.</summary>
    public static OwnedVariant Missing() => FromError(unchecked((int)0x80020004));

    /// <summary>
    /// VT_DECIMAL (T-INT-34): the 16-byte DECIMAL is written first at the declared overlay offsets, then the tag, so
    /// no zeroing of the apparent reserved words can damage scale, sign or the high word.
    /// </summary>
    public static OwnedVariant FromDecimal(DecimalValue value)
    {
        var result = new OwnedVariant();
        *result._cell = DecimalCell(value);
        result.State = OwnershipState.Owned;
        return result;
    }

    /// <summary>A VT_DECIMAL cell, which owns no resource (the 96-bit value lies inline in the VARIANT's 16 bytes).</summary>
    public static NativeVariant DecimalCell(DecimalValue value)
    {
        if (value.Scale > TargetDeclarations.WindowsX64.Decimal.MaxScale)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "DECIMAL scale is 0 to 28.");
        }
        NativeVariant cell = default;
        byte* b = (byte*)&cell;
        DecimalLayout d = TargetDeclarations.WindowsX64.Decimal;
        b[d.ScaleOffset] = value.Scale;
        b[d.SignOffset] = value.Negative ? d.NegativeSign : (byte)0;
        *(uint*)(b + d.High32Offset) = value.High32;
        *(ulong*)(b + d.Low64Offset) = value.Low64;
        cell.Vt = (ushort)VarType.Decimal;
        return cell;
    }

    /// <summary>
    /// VT_BSTR with exactly these payload bytes (T-INT-35/36): odd counts, embedded zeros and unpaired surrogates
    /// survive; the allocator writes its own two-byte terminator after them.
    /// </summary>
    public static OwnedVariant FromBstrBytes(ReadOnlySpan<byte> payload)
    {
        var result = new OwnedVariant();
        nint bstr;
        fixed (byte* p = payload)
        {
            // A non-null pointer, even for zero bytes, gives an allocated empty BSTR rather than a null one.
            byte one = 0;
            bstr = OleAut.SysAllocStringByteLen(payload.Length == 0 ? &one : p, (uint)payload.Length);
        }
        if (bstr == 0)
        {
            result.Dispose();
            throw new AutomationServiceException("SysAllocStringByteLen", OleAut.E_OUTOFMEMORY);
        }
        result._cell->Pointer = bstr;
        result._cell->Vt = (ushort)VarType.Bstr;
        result.State = OwnershipState.Owned;
        Foundation.Diagnostics.CostCounters counters = Foundation.Diagnostics.CostCounters.Current;
        counters.BstrAllocations++;
        counters.BstrPayloadBytes += payload.Length;
        return result;
    }

    /// <summary>VT_BSTR of these UTF-16 code units, exactly (lone surrogates included).</summary>
    public static OwnedVariant FromText(ReadOnlySpan<char> text) => FromBstrBytes(MemoryMarshal.AsBytes(text));

    /// <summary>VT_BSTR holding a null pointer: a different value from an allocated empty string (T-INT-36).</summary>
    public static OwnedVariant NullBstr() => Scalar(VarType.Bstr, v => v->Pointer = 0);

    /// <summary>Take ownership of a SAFEARRAY created for this value (its element type gives the tag).</summary>
    public static OwnedVariant TakeArray(nint safeArray, VarType elementType)
    {
        var result = new OwnedVariant();
        result._cell->Pointer = safeArray;
        result._cell->Vt = (ushort)(VarType.Array | elementType);
        result.State = OwnershipState.Owned;
        return result;
    }

    /// <summary>Take ownership of one interface reference the caller already owns (no AddRef here).</summary>
    public static OwnedVariant TakeObject(nint unknown, VarType objectType)
    {
        if (!VarTypes.IsObject(objectType) || VarTypes.IsArray(objectType))
        {
            throw new ArgumentException("VT_UNKNOWN or VT_DISPATCH expected.", nameof(objectType));
        }
        var result = new OwnedVariant();
        result._cell->Pointer = unknown;
        result._cell->Vt = (ushort)objectType;
        result.State = OwnershipState.Owned;
        return result;
    }

    /// <summary>Wrap bytes already constructed in a fresh temporary by a capture (internal construction path).</summary>
    internal static OwnedVariant Adopt(in NativeVariant constructed)
    {
        var result = new OwnedVariant();
        *result._cell = constructed;
        result.State = constructed.Vt == (ushort)VarType.Empty ? OwnershipState.Empty : OwnershipState.Owned;
        return result;
    }

    private static OwnedVariant Scalar(VarType type, ScalarFill fill)
    {
        var result = new OwnedVariant();
        fill(result._cell);
        result._cell->Vt = (ushort)type;
        result.State = OwnershipState.Owned;
        return result;
    }

    private delegate void ScalarFill(NativeVariant* cell);

    /// <summary>The exact payload bytes of a VT_BSTR, or null for a null BSTR (byte count from SysStringByteLen).</summary>
    public byte[]? BstrBytes()
    {
        Require(VarType.Bstr);
        nint bstr = _cell->Pointer;
        if (bstr == 0)
        {
            return null;
        }
        uint length = OleAut.SysStringByteLen(bstr);
        return new ReadOnlySpan<byte>((void*)bstr, checked((int)length)).ToArray();
    }

    public int AsInt32() { Require(VarType.I4); return *(int*)&_cell->Int64; }

    public double AsDouble() { Require(VarType.R8); return _cell->Double; }

    public DecimalValue AsDecimal()
    {
        Require(VarType.Decimal);
        byte* b = (byte*)_cell;
        DecimalLayout d = TargetDeclarations.WindowsX64.Decimal;
        return new DecimalValue(b[d.ScaleOffset], b[d.SignOffset] == d.NegativeSign, *(uint*)(b + d.High32Offset), *(ulong*)(b + d.Low64Offset));
    }

    private void Require(VarType type)
    {
        if ((VarType)Pointer->Vt != type)
        {
            throw new InvalidOperationException($"VARIANT holds {(VarType)_cell->Vt}, not {type}.");
        }
    }

    /// <summary>
    /// VariantCopy into a new owner: owned strings and arrays are copied, objects retained, and a ByRef reference
    /// stays a reference (it does not become a by-value snapshot).
    /// </summary>
    public OwnedVariant CopyPreservingReference() => CopyWith(&CopyPlain);

    /// <summary>VariantCopyInd into a new owner: the referenced value of a supported ByRef form.</summary>
    public OwnedVariant CopyIndirect() => CopyWith(&CopyInd);

    private static int CopyPlain(NativeVariant* d, NativeVariant* s) => OleAut.VariantCopy(d, s);

    private static int CopyInd(NativeVariant* d, NativeVariant* s) => OleAut.VariantCopyInd(d, s);

    private OwnedVariant CopyWith(delegate*<NativeVariant*, NativeVariant*, int> copy)
    {
        var result = new OwnedVariant();
        int hr = copy(result._cell, Pointer);
        if (hr != OleAut.S_OK)
        {
            result.Dispose();
            throw new AutomationServiceException("VariantCopy", hr);
        }
        result.State = result._cell->Vt == (ushort)VarType.Empty ? OwnershipState.Empty : OwnershipState.Owned;
        return result;
    }

    /// <summary>
    /// VariantChangeTypeEx to a type that owns no resource (a number, a date, a Boolean), into a caller's stack cell
    /// under an explicit <paramref name="lcid"/> (R-EXPR-1 EXPR-LOCALE, task 5zh9; II-10 §14): no object and no native
    /// cell (task v4n6). False with the HRESULT when the conversion fails.
    /// </summary>
    public static bool TryChangeScalar(NativeVariant* source, VarType target, uint lcid, out NativeVariant result, out int hr)
    {
        if (target is not (VarType.I2 or VarType.I4 or VarType.R4 or VarType.R8 or VarType.Currency or VarType.Date or VarType.Bool or VarType.UI1))
        {
            throw new ArgumentOutOfRangeException(nameof(target), "a scalar target that owns no resource");
        }
        NativeVariant cell = default;
        hr = OleAut.VariantChangeTypeEx(&cell, source, lcid, 0, (ushort)target);
        result = cell;
        return hr >= 0;
    }

    /// <summary>
    /// VariantChangeTypeEx of a borrowed value into a new owned one under an explicit <paramref name="lcid"/> (a
    /// VT_BYREF source is read through its reference), or null with the failing HRESULT (DISP_E_TYPEMISMATCH,
    /// DISP_E_OVERFLOW).
    /// </summary>
    public static OwnedVariant? ChangeType(NativeVariant* source, VarType target, uint lcid, out int hr) => ChangeType(source, target, lcid, 0, out hr);

    /// <summary>VariantChangeTypeEx under <paramref name="lcid"/> with <paramref name="flags"/> (VARIANT_ALPHABOOL 0x2: a VT_BOOL reads as "True"/"False").</summary>
    public static OwnedVariant? ChangeType(NativeVariant* source, VarType target, uint lcid, ushort flags, out int hr)
    {
        var result = new OwnedVariant();
        hr = OleAut.VariantChangeTypeEx(result._cell, source, lcid, flags, (ushort)target);
        if (hr < 0)
        {
            result.Dispose();
            return null;
        }
        result.State = OwnershipState.Owned;
        return result;
    }

    /// <summary>
    /// The final inert handoff into a caller's output cell (II-12 owned publication): the caller's cell receives the
    /// value and its ownership, this value becomes moved-from. No allocation or foreign call happens here.
    /// </summary>
    public void TransferTo(NativeVariant* callerCell)
    {
        if (State == OwnershipState.CleanupPending)
        {
            throw new InvalidOperationException("A value whose cleanup failed cannot be transferred; settle it first.");
        }
        *callerCell = *Pointer;
        OleAut.VariantInit(_cell);
        State = OwnershipState.MovedFrom;
    }

    /// <summary>The BSTR of a VT_BSTR value, handed to a caller's <c>BSTR*</c> output; this value becomes moved-from.</summary>
    public nint TransferBstr()
    {
        Require(VarType.Bstr);
        nint bstr = _cell->Pointer;
        OleAut.VariantInit(_cell);
        State = OwnershipState.MovedFrom;
        return bstr;
    }

    public void MoveTo(OwnedVariant destination)
    {
        if (destination.State != OwnershipState.Empty && destination.State != OwnershipState.MovedFrom)
        {
            throw new InvalidOperationException("The destination of a move must be empty.");
        }
        if (State == OwnershipState.CleanupPending)
        {
            throw new InvalidOperationException("A value whose cleanup failed cannot be moved; settle it first.");
        }
        *destination.Pointer = *Pointer;
        destination.State = State == OwnershipState.Owned ? OwnershipState.Owned : OwnershipState.Empty;
        OleAut.VariantInit(_cell);
        State = OwnershipState.MovedFrom;
    }

    /// <summary>
    /// Release the active arm's resources through VariantClear. On success the value is empty. On failure (a locked
    /// array) the resource stays owned in <see cref="OwnershipState.CleanupPending"/>: nothing is forgotten or freed twice.
    /// </summary>
    public int Clear()
    {
        if (State is OwnershipState.Empty or OwnershipState.MovedFrom)
        {
            return OleAut.S_OK;
        }
        int hr = OleAut.VariantClear(Pointer);
        if (hr == OleAut.S_OK)
        {
            State = OwnershipState.Empty;
            CleanupFailure = null;
        }
        else
        {
            State = OwnershipState.CleanupPending;
            CleanupFailure = hr;
        }
        return hr;
    }

    /// <summary>
    /// Clear and free the cell. A clear that fails hands the cell to <see cref="PendingCleanup"/>, which keeps the
    /// obligation (II-02 §4: failed cleanup remains an owned obligation, never an abandoned resource).
    /// </summary>
    public void Dispose()
    {
        if (_cell is null)
        {
            return;
        }
        if (Clear() != OleAut.S_OK)
        {
            PendingCleanup.Add(this);
            return;
        }
        NativeMemory.AlignedFree(_cell);
        _cell = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Finally free a cell held by <see cref="PendingCleanup"/> once its clear succeeds.</summary>
    internal bool TrySettle()
    {
        if (Clear() != OleAut.S_OK)
        {
            return false;
        }
        NativeMemory.AlignedFree(_cell);
        _cell = null;
        return true;
    }
}

/// <summary>Values whose clear failed and are still owned; retried explicitly, never dropped.</summary>
public static class PendingCleanup
{
    private static readonly List<OwnedVariant> Pending = [];

    public static int Count { get { lock (Pending) { return Pending.Count; } } }

    internal static void Add(OwnedVariant value)
    {
        lock (Pending)
        {
            Pending.Add(value);
        }
    }

    /// <summary>Retry every pending clear (outside any latch of the caller); returns how many remain.</summary>
    public static int RetryAll()
    {
        OwnedVariant[] snapshot;
        lock (Pending)
        {
            snapshot = [.. Pending];
        }
        foreach (OwnedVariant v in snapshot)
        {
            if (v.TrySettle())
            {
                lock (Pending)
                {
                    Pending.Remove(v);
                }
            }
        }
        return Count;
    }
}

/// <summary>A DECIMAL's fields: scale 0..28, sign, and the 96-bit magnitude as high 32 and low 64 bits.</summary>
public readonly record struct DecimalValue(byte Scale, bool Negative, uint High32, ulong Low64)
{
    /// <summary>The same value as a .NET decimal (an independent witness of the fields).</summary>
    public decimal ToDecimal() => new((int)(uint)Low64, (int)(uint)(Low64 >> 32), (int)High32, Negative, Scale);

    public static DecimalValue FromDecimal(decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        return new DecimalValue((byte)((bits[3] >> 16) & 0xFF), bits[3] < 0, (uint)bits[2], ((ulong)(uint)bits[1] << 32) | (uint)bits[0]);
    }
}