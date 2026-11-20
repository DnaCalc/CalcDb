using System.Runtime.InteropServices;
using CalcDb.Foundation;
using CalcDb.Values;
using CalcDb.Values.Temporary;

namespace CalcDb.Values.Tests;

/// <summary>A test that needs the platform's own oleaut32 (Windows); elsewhere it is reported as skipped, never as passed.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Needs the platform oleaut32 (Windows).";
        }
    }
}

/// <summary>
/// oleaut32-usage.md rule 5: the same calls through the temporary stand-ins and through the real oleaut32, compared byte for
/// byte. Each buffer is freed by the allocator that made it; the two never meet. A case the stand-ins do not provide must
/// answer NotAvailableOnPlatform, and the comparison counts how many cases it actually compared.
/// </summary>
public sealed unsafe class StandInComparisonTests
{
    private static OleAutTable Real()
    {
        Assert.True(OleAutTable.TryPlatform(out OleAutTable real));
        return real;
    }

    private static readonly OleAutTable Stand = StandIns.Table;

    /// <summary>Equal images, with both printed whole on a difference (the default message cuts long strings).</summary>
    private static void Same(string real, string standIn) => Assert.True(real == standIn, $"\nreal:     {real}\nstand-in: {standIn}");

    [WindowsFact]
    public void Process_binding_is_the_platform_on_windows() => Assert.Equal(OleAutBinding.Platform, OleAut.Binding);

    [WindowsFact]
    public void Bstr_bytes_lengths_and_terminator_match()
    {
        OleAutTable real = Real();
        foreach (string text in new[] { "", "A", "A\0B", "\uD800x", "trailing " })
        {
            byte[] payload = MemoryMarshal.AsBytes(text.AsSpan()).ToArray();
            foreach (int length in new[] { payload.Length, Math.Max(0, payload.Length - 1) })
            {
                Same(BstrImage(real, payload, length), BstrImage(Stand, payload, length));
            }
        }
        Assert.Equal(0u, Stand.SysStringByteLen(0));
        Assert.Equal(real.SysStringLen(0), Stand.SysStringLen(0));
    }

    /// <summary>Prefix, payload and terminator bytes, and both length functions, of one allocation; then it is freed.</summary>
    private static string BstrImage(OleAutTable t, byte[] payload, int length)
    {
        byte one = 0;
        nint b;
        fixed (byte* p = payload)
        {
            b = t.SysAllocStringByteLen(length == 0 ? &one : p, (uint)length);
        }
        try
        {
            byte[] image = new ReadOnlySpan<byte>((byte*)b - 4, 4 + length + 2).ToArray();
            return Convert.ToHexString(image) + $" bytes={t.SysStringByteLen(b)} units={t.SysStringLen(b)}";
        }
        finally
        {
            t.SysFreeString(b);
        }
    }

    [WindowsFact]
    public void VariantInit_clear_and_copy_leave_the_same_bytes()
    {
        OleAutTable real = Real();
        Same(InitImage(real), InitImage(Stand));
        Same(ClearImage(real, VarType.I4), ClearImage(Stand, VarType.I4));
        Same(ClearImage(real, VarType.Bstr), ClearImage(Stand, VarType.Bstr));
        Same(CopyImage(real), CopyImage(Stand));
    }

    private static string InitImage(OleAutTable t)
    {
        NativeVariant v;
        new Span<byte>(&v, 24).Fill(0xCD);
        t.VariantInit(&v);
        return Convert.ToHexString(new ReadOnlySpan<byte>(&v, 24));
    }

    private static string ClearImage(OleAutTable t, VarType type)
    {
        NativeVariant v;
        new Span<byte>(&v, 24).Fill(0xCD);
        v.Vt = (ushort)type;
        if (type == VarType.Bstr)
        {
            fixed (char* p = "xy")
            {
                v.Pointer = t.SysAllocStringByteLen((byte*)p, 4);
            }
        }
        int hr = t.VariantClear(&v);
        if (type == VarType.Bstr)
        {
            v.Pointer = 0; // the freed pointer's value is the allocator's, not compared
        }
        return OleAut.Hr(hr) + " " + Convert.ToHexString(new ReadOnlySpan<byte>(&v, 24));
    }

    private static string CopyImage(OleAutTable t)
    {
        NativeVariant src = default, dst = default;
        t.VariantInit(&src);
        t.VariantInit(&dst);
        src.Vt = (ushort)VarType.Bstr;
        fixed (char* p = "copy\0me")
        {
            src.Pointer = t.SysAllocStringByteLen((byte*)p, 14);
        }
        int hr = t.VariantCopy(&dst, &src);
        string image = $"{OleAut.Hr(hr)} vt={dst.Vt} same={dst.Pointer == src.Pointer} " + Convert.ToHexString(new ReadOnlySpan<byte>((byte*)dst.Pointer - 4, 4 + 14 + 2));
        Assert.Equal(OleAut.S_OK, t.VariantClear(&src));
        Assert.Equal(OleAut.S_OK, t.VariantClear(&dst));
        return image;
    }

    public static IEnumerable<object[]> ArrayCases()
    {
        VarType[] types = [VarType.UI1, VarType.I2, VarType.I4, VarType.R4, VarType.R8, VarType.Currency, VarType.Date, VarType.Bool,
            VarType.Bstr, VarType.Variant, VarType.Unknown, VarType.Dispatch, VarType.Decimal, VarType.Error, VarType.I8];
        foreach (VarType type in types)
        {
            yield return [type, new[] { (1, 3) }];
            yield return [type, new[] { (0, 2), (5, 3) }];
            yield return [type, new[] { (-1, 2), (0, 1), (3, 4) }];
        }
    }

    [WindowsFact]
    public void Safearray_descriptor_bounds_addressing_and_locks_match()
    {
        OleAutTable real = Real();
        int compared = 0;
        foreach (object[] c in ArrayCases())
        {
            var type = (VarType)c[0];
            var dims = ((int, int)[])c[1];
            Same(ArrayImage(real, type, dims), ArrayImage(Stand, type, dims));
            compared++;
        }
        Assert.Equal(45, compared);
    }

    /// <summary>Descriptor header and bounds, the hidden VARTYPE or IID, every API answer, every element's offset, the lock rules.</summary>
    private static string ArrayImage(OleAutTable t, VarType type, (int Lower, int Count)[] dims)
    {
        var bounds = stackalloc SafeArrayBound[dims.Length];
        for (int i = 0; i < dims.Length; i++)
        {
            bounds[i] = new SafeArrayBound { Lower = dims[i].Lower, Count = (uint)dims[i].Count };
        }
        nint psa = t.SafeArrayCreate((ushort)type, (uint)dims.Length, bounds);
        Assert.NotEqual(0, psa);
        var sb = new System.Text.StringBuilder();
        byte* d = (byte*)psa;
        sb.Append("head=").Append(Convert.ToHexString(new ReadOnlySpan<byte>(d, 12)));
        sb.Append(" bounds=").Append(Convert.ToHexString(new ReadOnlySpan<byte>(d + 24, 8 * dims.Length)));
        ushort features = *(ushort*)(d + 2);
        if ((features & 0x0040) != 0)
        {
            sb.Append(" iid=").Append(Convert.ToHexString(new ReadOnlySpan<byte>(d - 16, 16)));
        }
        else if ((features & 0x0080) != 0)
        {
            sb.Append(" vt=").Append(Convert.ToHexString(new ReadOnlySpan<byte>(d - 4, 4)));
        }
        sb.Append($" dim={t.SafeArrayGetDim(psa)} size={t.SafeArrayGetElemsize(psa)}");
        for (uint dim = 0; dim <= dims.Length + 1; dim++)
        {
            int lower = 0, upper = 0;
            int hl = t.SafeArrayGetLBound(psa, dim, &lower);
            int hu = t.SafeArrayGetUBound(psa, dim, &upper);
            sb.Append($" [{dim}] {OleAut.Hr(hl)} {lower} {OleAut.Hr(hu)} {upper}");
        }
        ushort vt = 0;
        sb.Append($" vartype={OleAut.Hr(t.SafeArrayGetVartype(psa, &vt))} {vt}");
        void* data;
        Assert.Equal(OleAut.S_OK, t.SafeArrayAccessData(psa, &data));
        sb.Append($" locked-destroy={OleAut.Hr(t.SafeArrayDestroy(psa))}");
        Assert.Equal(OleAut.S_OK, t.SafeArrayUnaccessData(psa));
        sb.Append($" unlock-at-zero={OleAut.Hr(t.SafeArrayUnlock(psa))}");
        // Every index tuple, plus one past each end of each stored position, in the stored pairing of indices to bounds.
        Span<int> index = stackalloc int[dims.Length];
        AppendOffsets(t, psa, (byte*)data, dims, index, 0, sb);
        for (int i = 0; i < dims.Length; i++)
        {
            for (int k = 0; k < dims.Length; k++)
            {
                index[k] = dims[k].Lower;
            }
            index[i] = dims[i].Lower + dims[i].Count;
            void* element;
            fixed (int* p = index)
            {
                sb.Append($" past{i}={OleAut.Hr(t.SafeArrayPtrOfIndex(psa, p, &element))}");
            }
        }
        Assert.Equal(OleAut.S_OK, t.SafeArrayDestroy(psa));
        return sb.ToString();
    }

    private static void AppendOffsets(OleAutTable t, nint psa, byte* data, (int Lower, int Count)[] dims, Span<int> index, int at, System.Text.StringBuilder sb)
    {
        if (at == dims.Length)
        {
            void* element;
            int hr;
            fixed (int* p = index)
            {
                hr = t.SafeArrayPtrOfIndex(psa, p, &element);
            }
            sb.Append($" {OleAut.Hr(hr)}@{(hr == 0 ? (long)((byte*)element - data) : -1)}");
            return;
        }
        for (int i = 0; i < dims[at].Count; i++)
        {
            index[at] = dims[at].Lower + i;
            AppendOffsets(t, psa, data, dims, index, at + 1, sb);
        }
    }

    [Fact]
    public void The_scalar_kernels_entry_points_are_not_available_in_the_stand_ins()
    {
        // W-02/shared-scalar-services: VariantChangeTypeEx and the Var* arithmetic and comparison have no stand-in yet.
        NativeVariant a = default, b = default, r = default;
        a.Vt = b.Vt = (ushort)VarType.I4;
        Assert.Equal(OleAut.NotAvailableOnPlatform, Stand.VariantChangeTypeEx(&r, &a, 7177, 0, (ushort)VarType.Bstr));
        foreach (nint f in new[] { (nint)Stand.VarAdd, (nint)Stand.VarSub, (nint)Stand.VarMul, (nint)Stand.VarDiv, (nint)Stand.VarIdiv, (nint)Stand.VarMod, (nint)Stand.VarPow })
        {
            Assert.Equal(OleAut.NotAvailableOnPlatform, ((delegate* unmanaged<NativeVariant*, NativeVariant*, NativeVariant*, int>)f)(&a, &b, &r));
        }
        foreach (nint f in new[] { (nint)Stand.VarNeg, (nint)Stand.VarAbs, (nint)Stand.VarInt, (nint)Stand.VarFix })
        {
            Assert.Equal(OleAut.NotAvailableOnPlatform, ((delegate* unmanaged<NativeVariant*, NativeVariant*, int>)f)(&a, &r));
        }
        Assert.Equal(OleAut.NotAvailableOnPlatform, Stand.VarRound(&a, 0, &r));
        Assert.Equal(OleAut.NotAvailableOnPlatform, Stand.VarCmp(&a, &b, 7177, 0));
        Assert.Equal(VarType.Empty, (VarType)r.Vt);
    }

    [WindowsFact]
    public void VariantChangeType_matches_where_the_stand_in_answers()
    {
        OleAutTable real = Real();
        VarType[] targets = [VarType.I2, VarType.I4, VarType.UI1, VarType.R4, VarType.R8, VarType.Currency, VarType.Bool, VarType.Bstr];
        int compared = 0, unavailable = 0;
        foreach (NativeVariant source in Sources())
        {
            foreach (VarType target in targets)
            {
                NativeVariant src = source;
                NativeVariant a = default, b = default;
                int hb = Stand.VariantChangeType(&b, &src, 0, (ushort)target);
                if (hb == OleAut.NotAvailableOnPlatform)
                {
                    unavailable++;
                    continue;
                }
                int ha = real.VariantChangeType(&a, &src, 0, (ushort)target);
                string label = $"{(VarType)source.Vt} {Payload(&src, real)} -> {target}";
                Assert.Equal((label, OleAut.Hr(ha), Payload(&a, real)), (label, OleAut.Hr(hb), Payload(&b, Stand)));
                if (ha == 0)
                {
                    _ = real.VariantClear(&a);
                }
                if (hb == 0)
                {
                    _ = Stand.VariantClear(&b);
                }
                compared++;
            }
        }
        Assert.True(compared >= 200, $"compared {compared}, not available {unavailable}");
    }

    private static NativeVariant V(VarType t, long bits) => new() { Vt = (ushort)t, Int64 = bits };

    private static NativeVariant D(double x) => new() { Vt = (ushort)VarType.R8, Double = x };

    private static NativeVariant F(float x) => new() { Vt = (ushort)VarType.R4, Int64 = (uint)BitConverter.SingleToInt32Bits(x) };

    private static IEnumerable<NativeVariant> Sources()
    {
        yield return V(VarType.Empty, 0);
        yield return V(VarType.Null, 0);
        foreach (short s in new short[] { 0, 1, -1, 255, 256, short.MinValue, short.MaxValue })
        {
            yield return V(VarType.I2, (ushort)s);
        }
        foreach (int i in new[] { 0, 7, -7, 32767, 32768, -32769, int.MinValue, int.MaxValue })
        {
            yield return V(VarType.I4, (uint)i);
        }
        yield return V(VarType.UI1, 0);
        yield return V(VarType.UI1, 255);
        yield return V(VarType.Bool, 0);
        yield return V(VarType.Bool, 0xFFFF);
        foreach (double x in new[] { 0.0, -0.0, 0.5, 1.5, 2.5, -0.5, -1.5, -2.5, 254.5, 255.5, 32766.5, 32767.5, -32768.5, 2147483646.5, 2147483647.5, -2147483648.5, 1e300, -1e300, 3.4e38, 3.5e38, 922337203685477.5807, 922337203685478.0, 1.00005 })
        {
            yield return D(x);
        }
        foreach (float x in new[] { 0.5f, 2.5f, -2.5f, 3.4e38f })
        {
            yield return F(x);
        }
        foreach (long cy in new long[] { 0, 5000, 15000, 25000, -5000, -15000, -25000, 10001, 2147483647_5000, long.MaxValue, long.MinValue, 2555000, 327675000 })
        {
            yield return V(VarType.Currency, cy);
        }
    }

    private static string Payload(NativeVariant* v, OleAutTable t) => (VarType)v->Vt switch
    {
        VarType.I2 or VarType.Bool => $"{v->Vt}:{*(short*)&v->Int64}",
        VarType.I4 => $"{v->Vt}:{*(int*)&v->Int64}",
        VarType.UI1 => $"{v->Vt}:{*(byte*)&v->Int64}",
        VarType.R4 => $"{v->Vt}:{BitConverter.SingleToInt32Bits(*(float*)&v->Int64):X8}",
        VarType.R8 => $"{v->Vt}:{v->Int64:X16}",
        VarType.Currency => $"{v->Vt}:{v->Int64}",
        VarType.Bstr => $"{v->Vt}:'{new string((char*)v->Pointer, 0, (int)t.SysStringLen(v->Pointer))}'",
        _ => $"{v->Vt}",
    };
}
