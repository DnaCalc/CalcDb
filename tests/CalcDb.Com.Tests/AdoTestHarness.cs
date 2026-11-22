using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// Shared raw-COM <c>IDispatch</c> harness, CLSID/IID constants, fixture helpers, and error assertions
/// used across the published-module ADO tests (<see cref="AdoExecuteAndRecordsetTests"/> and
/// <see cref="AdoImmediateEditTests"/>).
/// </summary>
internal static unsafe class AdoTestHarness
{
    public static readonly Guid OwnEngine = new("D44A3A3E-4FD9-4426-A7B7-8351E8421EBF");
    public static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    public static readonly Guid OwnCommand = new("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5");
    public static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");

    public static readonly Guid IidColumnsInfo = new("0C733A11-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidCommandText = new("0C733A26-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidTransactionLocal = new("0C733A5F-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidCommand = new("0C733A63-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidRowsetUpdate = new("0C733A6D-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidRowset = new("0C733A7C-2A1C-11CE-ADE5-00AA0044773D");
    public static readonly Guid IidRecordset = new("00001556-0000-0010-8000-00AA006D2EA4");
    public static readonly Guid IidField = new("00001569-0000-0010-8000-00AA006D2EA4");
    public static readonly Guid IidEnumVariant = new("00020404-0000-0000-C000-000000000046");

    public const string Ace = "Microsoft.ACE.OLEDB.12.0";

    public sealed class MissingValue;
    public static readonly MissingValue Missing = new();
    public readonly record struct Dispatch(nint Pointer);
    public readonly record struct CurrencyValue(decimal Amount);
    public readonly record struct Error(int Scode, string? Source, string? Description, string? HelpFile, uint HelpContext, Guid InfoGuid);
    public readonly record struct Outcome(object? Value, Error? Failure);

    public static string CopyFixture(ArtifactStore store, string sha256, string dir, string fileName)
    {
        string path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, store.ReadAll(sha256));
        return path;
    }

    public static nint OpenAceConnection(string dbPath, int? mode = null)
    {
        nint cn = New(OwnConnection);
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        nint props = (nint)Get(cn, "Properties")!;
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        Assert.Null(Put(ds, "Value", dbPath).Failure);
        Release(ds);
        Release(props);
        if (mode is { } m)
        {
            Assert.Null(Put(cn, "Mode", m).Failure);
        }
        Assert.Null(Call(cn, "Open").Failure);
        Assert.Equal(1, Get(cn, "State"));
        return cn;
    }

    public static void AssertError(
        Outcome outcome,
        int expectedScode,
        string? expectedSource = null,
        string? expectedDescription = null,
        uint? expectedHelpContext = null,
        Guid? expectedInfoGuid = null)
    {
        Assert.NotNull(outcome.Failure);
        Error err = outcome.Failure!.Value;
        Assert.Equal(expectedScode, err.Scode);
        if (expectedSource is not null)
        {
            Assert.Equal(expectedSource, err.Source);
        }
        if (expectedDescription is not null)
        {
            Assert.Equal(expectedDescription, err.Description);
        }
        if (expectedHelpContext is { } hc)
        {
            Assert.Equal(hc, err.HelpContext);
        }
        if (expectedInfoGuid is { } iid)
        {
            Assert.Equal(iid, err.InfoGuid);
        }
    }

    public static void AssertConnectionError0(
        nint cn,
        int expectedNumber,
        string? expectedSqlState = null,
        int? expectedNativeError = null,
        string? expectedSource = null,
        string? expectedDescription = null)
    {
        nint errs = (nint)Get(cn, "Errors")!;
        try
        {
            Assert.Equal(1, Get(errs, "Count"));
            nint err0 = (nint)Get(errs, "Item", 0)!;
            try
            {
                Assert.Equal(expectedNumber, Get(err0, "Number"));
                if (expectedSqlState is not null)
                {
                    Assert.Equal(expectedSqlState, Get(err0, "SQLState") ?? "");
                }
                if (expectedNativeError is { } ne)
                {
                    Assert.Equal(ne, Get(err0, "NativeError"));
                }
                if (expectedSource is not null)
                {
                    Assert.Equal(expectedSource, Get(err0, "Source"));
                }
                if (expectedDescription is not null)
                {
                    Assert.Equal(expectedDescription, Get(err0, "Description"));
                }
            }
            finally
            {
                Release(err0);
            }
        }
        finally
        {
            Release(errs);
        }
    }

    public static object? Get(nint target, string member, params object?[] args)
    {
        Outcome o = Invoke(target, member, 2, args);
        Assert.True(o.Failure is null, $"{member} failed: {o.Failure}");
        return o.Value;
    }

    public static Outcome TryGet(nint target, string member, params object?[] args) => Invoke(target, member, 2, args);

    public static Outcome Put(nint target, string member, object? value) => Invoke(target, member, 4, value);

    public static Outcome Call(nint target, string member, params object?[] args) => Invoke(target, member, 1, args);

    public static int GetDispId(nint target, string member)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        int dispId;
        fixed (char* name = member)
        {
            char* pName = name;
            int hr = ((delegate* unmanaged<nint, Guid*, char**, uint, uint, int*, int>)vt[5])(target, &iidNull, &pName, 1, 1033, &dispId);
            Assert.Equal(0, hr);
        }
        return dispId;
    }

    public static nint New(Guid clsid)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, IidIDispatch, out nint created));
        return created;
    }

    public static Outcome Invoke(nint target, string member, ushort flags, params object?[] args)
    {
        int dispId = GetDispId(target, member);
        NativeVariant* raw = stackalloc NativeVariant[Math.Max(args.Length, 1)];
        for (int i = 0; i < args.Length; i++)
        {
            raw[args.Length - 1 - i] = VariantOf(args[i]);
        }
        int namedPut = -3;
        try
        {
            return InvokeDispId(target, dispId, flags, args.Length == 0 ? null : raw, (uint)args.Length, flags == 4 ? 1u : 0u, flags == 4 ? &namedPut : null);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (raw[i].Vt == (ushort)VarType.Bstr && raw[i].Pointer != 0)
                {
                    Marshal.FreeBSTR(raw[i].Pointer);
                }
                else if (raw[i].Vt == (ushort)(VarType.Array | VarType.UI1) && raw[i].Pointer != 0)
                {
                    _ = SafeArrayDestroy(raw[i].Pointer);
                }
            }
        }
    }

    public static Outcome InvokeDispId(nint target, int dispId, ushort flags, NativeVariant* raw, uint argCount, uint namedCount, int* namedIds)
    {
        nint* vt = *(nint**)target;
        Guid iidNull = Guid.Empty;
        DispParams dp = new()
        {
            Args = (nint)raw,
            NamedArgs = (nint)namedIds,
            ArgCount = argCount,
            NamedArgCount = namedCount,
        };
        NativeVariant result = default;
        ExcepInfo ex = default;
        uint argErr;
        if (GetErrorInfo(0, out nint staleInfo) == 0 && staleInfo != 0)
        {
            Release(staleInfo);
        }
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)vt[6])(
            target, dispId, &iidNull, 1033, flags, &dp, &result, &ex, &argErr);
        if (hr == 0)
        {
            return new(Value(result), null);
        }
        Guid infoGuid = Guid.Empty;
        if (GetErrorInfo(0, out nint info) == 0 && info != 0)
        {
            nint* ivt = *(nint**)info;
            ((delegate* unmanaged<nint, Guid*, int>)ivt[3])(info, &infoGuid);
            Release(info);
        }
        string? src = TakeBstr(ex.Source);
        string? desc = TakeBstr(ex.Description);
        string? help = TakeBstr(ex.HelpFile);
        return new(null, new(hr == HResults.DISP_E_EXCEPTION ? ex.Scode : hr, src, desc, help, ex.HelpContext, infoGuid));
    }

    public static string? TakeBstr(nint bstr)
    {
        if (bstr == 0)
        {
            return null;
        }
        string s = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        return s;
    }

    public static NativeVariant VariantOf(object? value)
    {
        NativeVariant v = default;
        switch (value)
        {
            case null:
                v.Vt = (ushort)VarType.Empty;
                break;
            case byte u8:
                v.Vt = (ushort)VarType.UI1;
                v.Int64 = u8;
                break;
            case short s16:
                v.Vt = (ushort)VarType.I2;
                v.Int64 = unchecked((ushort)s16);
                break;
            case int i:
                v.Vt = (ushort)VarType.I4;
                v.Int64 = (uint)i;
                break;
            case float f:
                v.Vt = (ushort)VarType.R4;
                v.Int64 = (uint)BitConverter.SingleToInt32Bits(f);
                break;
            case double d:
                v.Vt = (ushort)VarType.R8;
                v.Int64 = BitConverter.DoubleToInt64Bits(d);
                break;
            case CurrencyValue cy:
                v.Vt = (ushort)VarType.Currency;
                v.Int64 = decimal.ToOACurrency(cy.Amount);
                break;
            case DateTime dt:
                v.Vt = (ushort)VarType.Date;
                v.Int64 = BitConverter.DoubleToInt64Bits(dt.ToOADate());
                break;
            case bool b:
                v.Vt = (ushort)VarType.Bool;
                v.Int64 = b ? unchecked((ushort)(-1)) : 0;
                break;
            case string s:
                v.Vt = (ushort)VarType.Bstr;
                v.Pointer = Marshal.StringToBSTR(s);
                break;
            case byte[] bytes:
            {
                nint psa = SafeArrayCreateVector((ushort)VarType.UI1, 0, (uint)bytes.Length);
                Assert.NotEqual((nint)0, psa);
                Assert.Equal(0, SafeArrayAccessData(psa, out nint data));
                try
                {
                    if (bytes.Length > 0)
                    {
                        Marshal.Copy(bytes, 0, data, bytes.Length);
                    }
                }
                finally
                {
                    _ = SafeArrayUnaccessData(psa);
                }
                v.Vt = (ushort)(VarType.Array | VarType.UI1);
                v.Pointer = psa;
                break;
            }
            case DBNull:
                v.Vt = (ushort)VarType.Null;
                break;
            case Dispatch d:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = d.Pointer;
                break;
            case nint p:
                v.Vt = (ushort)VarType.Dispatch;
                v.Pointer = p;
                break;
            default:
                Assert.Same(Missing, value);
                v.Vt = (ushort)VarType.Error;
                v.Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND);
                break;
        }
        return v;
    }

    public static object? Value(NativeVariant v)
    {
        switch ((VarType)v.Vt)
        {
            case VarType.Empty:
                return null;
            case VarType.Null:
                return DBNull.Value;
            case VarType.I4:
                return (int)v.Int64;
            case VarType.I2:
                return (short)v.Int64;
            case VarType.I8:
                return v.Int64;
            case VarType.UI1:
                return (byte)v.Int64;
            case VarType.R4:
                return BitConverter.Int32BitsToSingle((int)v.Int64);
            case VarType.R8:
                return BitConverter.Int64BitsToDouble(v.Int64);
            case VarType.Currency:
                return new CurrencyValue(decimal.FromOACurrency(v.Int64));
            case VarType.Date:
                return DateTime.FromOADate(BitConverter.Int64BitsToDouble(v.Int64));
            case VarType.Decimal:
            {
                // OLE DECIMAL shares the 16-byte VARIANT layout: [wReserved(2), scale(1), sign(1), Hi32(4), Lo32(4), Mid32(4)].
                byte* raw = (byte*)&v;
                byte scale = raw[2];
                bool isNegative = (raw[3] & 0x80) != 0;
                int hi = *(int*)(raw + 4);
                int lo = *(int*)(raw + 8);
                int mid = *(int*)(raw + 12);
                return new decimal(lo, mid, hi, isNegative, scale);
            }
            case VarType.Bool:
                return (short)v.Int64 != 0;
            case VarType.Bstr when v.Pointer == 0:
                return null;
            case VarType.Bstr:
                string s = Marshal.PtrToStringBSTR(v.Pointer);
                Marshal.FreeBSTR(v.Pointer);
                return s;
            case VarType.Array | VarType.UI1:
            {
                nint psa = v.Pointer;
                if (psa == 0)
                {
                    return Array.Empty<byte>();
                }
                try
                {
                    Assert.Equal(0, SafeArrayGetLBound(psa, 1, out int lb));
                    Assert.Equal(0, SafeArrayGetUBound(psa, 1, out int ub));
                    int len = ub >= lb ? (ub - lb + 1) : 0;
                    byte[] result = new byte[len];
                    if (len > 0)
                    {
                        Assert.Equal(0, SafeArrayAccessData(psa, out nint data));
                        try
                        {
                            Marshal.Copy(data, result, 0, len);
                        }
                        finally
                        {
                            _ = SafeArrayUnaccessData(psa);
                        }
                    }
                    return result;
                }
                finally
                {
                    _ = SafeArrayDestroy(psa);
                }
            }
            case VarType.Dispatch:
            case VarType.Unknown:
                return v.Pointer;
            default:
                throw new InvalidOperationException("unexpected result type " + (VarType)v.Vt);
        }
    }

    [DllImport("oleaut32.dll")]
    private static extern nint SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(nint psa, out nint ppvData);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(nint psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(nint psa, uint nDim, out int plLbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(nint psa, uint nDim, out int plUbound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(nint psa);
}
