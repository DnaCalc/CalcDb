using System.Runtime.InteropServices;
using CalcDb.Com.Com;
using CalcDb.Foundation;
using CalcDb.Values;
using static CalcDb.Com.Tests.NativeRoute;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-09/construction-policy through the real caller route: CalcDb's own ADO classes from the published CalcDb.dll under
/// CalcDb.Own.manifest, called through raw IDispatch (and the vtable) on an STA. Every expected value is ACE's answer
/// in the cited record (R-ADO-1 ADO-CONN, ADO-PROVIDER, ADO-PROPS, ADO-PARAM; R-ACT-1 ACT-ADO-SCOPE); a route outside
/// scope is the named NotModelled.AdoProvider refusal, and a data path the named not-implemented outcome, never a match.
/// Covers the construction and closed-state cases of T-OBJ-31..37/56 and T-INT-61/62 (own classes, IErrorInfo).
/// </summary>
[Collection(PublishedModuleTests.Name)]
public sealed unsafe class AdoConstructionTests
{
    private static readonly Guid OwnConnection = new("B29F5595-8BF1-4AA8-9397-1F2F10F3A101");
    private static readonly Guid OwnRecordset = new("892D19AC-84CA-4F73-8A3E-00B7C066245A");
    private static readonly Guid OwnCommand = new("7AF35BF5-1686-4FB7-AA3E-9DC4EF68ECD5");
    private static readonly Guid OwnStream = new("95C5FB19-3B72-4FFA-868F-12A68495FF37");
    private static readonly Guid OwnRecord = new("2CA37905-9986-469E-8E8D-8E14E85E606E");
    private static readonly Guid IidConnection = new("00001550-0000-0010-8000-00AA006D2EA4");
    private static readonly Guid IidCommand = new("986761E8-7269-4890-AA65-AD7C03697A6D");
    private static readonly Guid IidParameters = new("0000150D-0000-0010-8000-00AA006D2EA4");
    private const string Ace = "Microsoft.ACE.OLEDB.12.0";
    private const int ObjectClosed = unchecked((int)0x800A0E78);

    [Fact]
    public void CalcDb_ADO_objects_are_constructed_and_closed_as_ACE_answers()
    {
        Assert.True(File.Exists(PublishedModule), $"publish first: dotnet publish src/CalcDb.Com -r win-x64 -c Release -o artifacts/native ({PublishedModule} is missing)");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scope = new Scope(Path.Combine(Path.GetDirectoryName(PublishedModule)!, "CalcDb.Own.manifest"));
                ConnectionDefaultsAndClosedState();
                ProviderRoutesAndPin();
                CommandAndParameters();
                RecordsetStreamRecord();
                // ACT-ADO-SCOPE: nothing here reached ACE or system ADO.
                Assert.False(IsLoaded("msado15.dll"));
                Assert.False(IsLoaded("ACEOLEDB.DLL"));
                Assert.False(IsLoaded("ACECORE.DLL"));
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    /// <summary>r-str-ado-act/act-ado-classes, r-str-ado-act/ado-open-provider-property (closed defaults), ado-provider-non-ace.</summary>
    private static void ConnectionDefaultsAndClosedState()
    {
        nint cn = New(OwnConnection);
        Assert.Equal(Path.GetFullPath(PublishedModule), ModuleOf(Vtable(cn)[6]), ignoreCase: true);
        Assert.Equal(0, Get(cn, "State"));
        Assert.Equal("10.0", Get(cn, "Version"));
        Assert.Equal("MSDASQL", Get(cn, "Provider"));
        Assert.Null(Get(cn, "ConnectionString")); // a null BSTR until assigned (r-str-ado-act/ado-open-provider-property)
        Assert.Equal(0, Get(cn, "Mode"));
        Assert.Equal(2, Get(cn, "CursorLocation"));
        Assert.Equal(30, Get(cn, "CommandTimeout"));
        Assert.Equal(15, Get(cn, "ConnectionTimeout"));
        Assert.Equal(4096, Get(cn, "IsolationLevel"));
        Assert.Equal(0, Get(cn, "Attributes"));
        nint errors = (nint)Get(cn, "Errors")!;
        Assert.Equal(0, Get(errors, "Count"));
        Assert.Equal(errors, (nint)Get(cn, "Errors")!); // one Errors per Connection
        AssertAdoError(Invoke(errors, "Item", 2, 0), unchecked((int)0x800A0CC1), "ADODB.Errors", 1240649, new Guid("00000501-0000-0010-8000-00AA006D2EA4"));

        // Closed-state failures: EXCEPINFO with ADO270.CHM and an IErrorInfo keyed to _Connection (T-INT-61/62).
        AssertAdoError(Call(cn, "Close"), ObjectClosed, "ADODB.Connection", 1240653, IidConnection);
        AssertAdoError(Invoke(cn, "DefaultDatabase", 2), ObjectClosed, "ADODB.Connection", 1240653, IidConnection);
        AssertAdoError(Call(cn, "Execute", "SELECT 1", Missing, 1), ObjectClosed, "ADODB.Connection", 1240653, IidConnection);
        AssertAdoError(Put(cn, "Mode", 99), unchecked((int)0x800A0C93), "ADODB.Connection", 1240646, IidConnection);
        AssertAdoError(Put(cn, "CommandTimeout", -1), unchecked((int)0x800A0BB9), "ADODB.Connection", 1240641, IidConnection);
        Assert.Equal(0, Get(cn, "Mode"));
        Assert.Null(Put(cn, "Mode", 1).Failure);
        Assert.Null(Put(cn, "CursorLocation", 3).Failure);
        Assert.Equal(1, Get(cn, "Mode"));
        Assert.Equal(3, Get(cn, "CursorLocation"));

        // The default provider is refused by name at Open and counted; the Connection stays closed, its Errors empty.
        AssertRefused(Call(cn, "Open"));
        Assert.Equal(0, Get(cn, "State"));
        Assert.Equal(0, Get(errors, "Count"));
        // A bare path is an ODBC DSN to ADO (r-ado-1b/conn-cs-bare-path): still the default provider.
        Assert.Null(Put(cn, "ConnectionString", @"C:\nowhere\x.accdb").Failure);
        Assert.Equal("MSDASQL", Get(cn, "Provider"));
        AssertRefused(Call(cn, "Open"));

        // The typed (vtable) route reaches the same operations: get_State (offset 272), get_Version (112).
        Assert.Equal(0, QueryInterface(cn, IidConnection, out nint dual));
        int state = -1;
        Assert.Equal(0, ((delegate* unmanaged<nint, int*, int>)Vtable(dual)[272 / 8])(dual, &state));
        Assert.Equal(0, state);
        nint version = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(dual)[112 / 8])(dual, &version));
        Assert.Equal("10.0", Marshal.PtrToStringBSTR(version));
        Marshal.FreeBSTR(version);
        // Type information is the captured ADODB 6.1 library's _Connection.
        nint ti = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, uint, uint, nint*, int>)Vtable(cn)[4])(cn, 0, 0x0409, &ti));
        Assert.Equal(IidConnection, *(Guid*)TypeAttr(ti, out nint attr));
        ((delegate* unmanaged<nint, nint, void>)Vtable(ti)[19])(ti, attr);
        Release(ti);
        Release(dual);
        Release(errors);
        Release(cn);
    }

    /// <summary>
    /// ADO-CONN and ADO-PROPS: the Provider key and property before Open, the ACE initialization properties and the pin
    /// a Properties read makes (r-fix-wire-ado/ado-provider-pin-isolated, r-q-ado-misc/props-conn-inventory,
    /// r-str-ado-act/ado-connstring-keys).
    /// </summary>
    private static void ProviderRoutesAndPin()
    {
        // Properties of the default provider are refused, and nothing is pinned: Provider can still be assigned.
        nint fresh = New(OwnConnection);
        AssertRefused(Invoke(fresh, "Properties", 2));
        Assert.Null(Put(fresh, "Provider", Ace).Failure);
        Assert.Equal(Ace, Get(fresh, "Provider"));
        Release(fresh);

        // The Provider getter shows the connection string's key as written, quotes kept.
        nint quoted = New(OwnConnection);
        string text = "Provider=\"Microsoft.ACE.OLEDB.12.0\";Mode='Read'";
        Assert.Null(Put(quoted, "ConnectionString", text).Failure);
        Assert.Equal(text, Get(quoted, "ConnectionString"));
        Assert.Equal("\"Microsoft.ACE.OLEDB.12.0\"", Get(quoted, "Provider"));
        Release(quoted);

        // A bare path after the ACE property keeps ACE: keyword absence does not override it.
        nint cn = New(OwnConnection);
        Assert.Null(Put(cn, "Provider", Ace).Failure);
        Assert.Null(Put(cn, "ConnectionString", @"C:\nowhere\x.accdb").Failure);
        Assert.Equal(Ace, Get(cn, "Provider"));
        nint props = (nint)Get(cn, "Properties")!;
        Assert.Equal(31, Get(props, "Count"));
        Assert.Equal(props, (nint)Get(cn, "Properties")!);
        nint first = (nint)Get(props, "Item", 0)!;
        Assert.Equal("Cache Authentication", Get(first, "Name"));
        Assert.Equal(11, Get(first, "Type"));
        Assert.Equal(513, Get(first, "Attributes"));
        Assert.Equal(true, Get(first, "Value"));
        nint ds = (nint)Get(props, "Item", "Data Source")!;
        Assert.Equal(ds, (nint)Get(props, "Item", 5)!); // one wrapper per entry
        Assert.Equal("", Get(ds, "Value"));
        Assert.Null(Put(ds, "Value", @"inputs\main.accdb").Failure);
        Assert.Equal(@"inputs\main.accdb", Get(ds, "Value"));
        nint window = (nint)Get(props, "Item", "Window Handle")!;
        Assert.Equal(0L, Get(window, "Value"));
        AssertAdoError(Invoke(props, "Item", 2, "No Such Property"), unchecked((int)0x800A0CC1), "ADODB.Properties", 1240649, Guid.Empty);
        // Reading Properties pinned ACE: another provider is now refused 0x800A0C94.
        AssertAdoError(Put(cn, "Provider", "MSPersist"), unchecked((int)0x800A0C94), "ADODB.Connection", 1241171, IidConnection);
        // Both ConnectionString (bare path) and Properties("Data Source") were set: conflict is refused by name.
        Outcome open = Call(cn, "Open");
        Assert.True(open.Failure is not null);
        Assert.Equal(HResults.E_NOTIMPL, open.Failure.Value.Scode);
        Assert.Equal(0, Get(cn, "State"));
        foreach (nint p in new[] { window, ds, first, props, cn })
        {
            Release(p);
        }
    }

    /// <summary>ADO-PARAM, ADO-SESSION: r-ado-1b/cmd-before-connection, param-create-forms, param-connection-change.</summary>
    private static void CommandAndParameters()
    {
        nint cmd = New(OwnCommand);
        Assert.Equal(0, Get(cmd, "State"));
        Assert.Equal(8, Get(cmd, "CommandType"));
        Assert.Null(Get(cmd, "CommandText")); // a null BSTR
        Assert.Equal(30, Get(cmd, "CommandTimeout"));
        Assert.Equal(false, Get(cmd, "Prepared"));
        Assert.Equal(false, Get(cmd, "NamedParameters"));
        Assert.Equal((nint)0, (nint)Get(cmd, "ActiveConnection")!);
        nint prs = (nint)Get(cmd, "Parameters")!;
        Assert.Equal(0, Get(prs, "Count"));
        Assert.Equal(0, GetIds(prs, "Append", out int appendId));
        Assert.Equal(3, appendId); // ACE's live DISPID, not the library's 1610809344
        Assert.Null(Call(prs, "Refresh").Failure);

        nint untyped = (nint)Call(cmd, "CreateParameter").Value!;
        Assert.Equal("", Get(untyped, "Name"));
        Assert.Equal(0, Get(untyped, "Type"));
        Assert.Equal(1, Get(untyped, "Direction"));
        Assert.Equal(0, Get(untyped, "Size"));
        Assert.Equal((byte)0, Get(untyped, "Precision"));
        Assert.Equal((byte)0, Get(untyped, "NumericScale"));
        Assert.Equal(0, Get(untyped, "Attributes"));
        Assert.Null(Get(untyped, "Value"));
        AssertAdoError(Call(prs, "Append", untyped), unchecked((int)0x800A0E7C), "ADODB.Parameters", 1240657, IidParameters);
        nint text0 = (nint)Call(cmd, "CreateParameter", "x", 202, 1, Missing, "abc").Value!;
        Assert.Equal("abc", Get(text0, "Value"));
        AssertAdoError(Call(prs, "Append", text0), unchecked((int)0x800A0E7C), "ADODB.Parameters", 1240657, IidParameters);
        AssertAdoError(Call(cmd, "CreateParameter", "x", 9999, 1), unchecked((int)0x800A0BB9), "ADODB.Command", 1240641, IidCommand);
        AssertAdoError(Call(cmd, "CreateParameter", "x", 3, 99), unchecked((int)0x800A0BB9), "ADODB.Command", 1240641, IidCommand);
        AssertAdoError(Call(cmd, "CreateParameter", "x", 3, 1, 0, "abc"), unchecked((int)0x800A0D5D), "ADODB.Command", 1240652, IidCommand);
        AssertAdoError(Call(cmd, "CreateParameter", "x", 202, 1, 2, "abcdef"), unchecked((int)0x800A0D5D), "ADODB.Command", 1240652, IidCommand);

        nint px = (nint)Call(cmd, "CreateParameter", "x", 3, 1, Missing, 1).Value!;
        Assert.Equal(1, Get(px, "Value"));
        Assert.Null(Call(prs, "Append", px).Failure);
        Assert.Equal(1, Get(prs, "Count"));
        Assert.Equal(px, (nint)Get(prs, "Item", "x")!);
        AssertAdoError(Invoke(prs, "Item", 2, "nope"), unchecked((int)0x800A0CC1), "ADODB.Parameters", 1240649, IidParameters);
        AssertAdoError(Put(px, "Name", "y"), unchecked((int)0x800A0C93), "ADODB.Parameter", 1240646, new Guid("0000150C-0000-0010-8000-00AA006D2EA4"));
        // A Long parameter takes " 20 " and refuses "abc", keeping its value.
        Assert.Null(Put(px, "Value", " 20 ").Failure);
        Assert.Equal(20, Get(px, "Value"));
        AssertAdoError(Put(px, "Value", "abc"), unchecked((int)0x800A0D5D), "ADODB.Parameter", 1240652, new Guid("0000150C-0000-0010-8000-00AA006D2EA4"));
        Assert.Equal(20, Get(px, "Value"));

        AssertAdoError(Call(cmd, "Execute"), unchecked((int)0x800A0E7D), "ADODB.Command", 1240658, IidCommand,
            "The connection cannot be used to perform this operation. It is either closed or invalid in this context.");
        Assert.Null(Put(cmd, "CommandText", "SELECT ID FROM T WHERE L = ?").Failure);
        Assert.Null(Call(prs, "Refresh").Failure);
        Assert.Equal(1, Get(prs, "Count"));
        // A fresh default-provider Connection cannot be the Command's session.
        nint cn = New(OwnConnection);
        AssertAdoError(Put(cmd, "ActiveConnection", new Dispatch(cn)), unchecked((int)0x800A0E7D), "ADODB.Command", 1240658, IidCommand,
            "Requested operation requires an OLE DB Session object, which is not supported by the current provider.");
        foreach (nint p in new[] { cn, px, text0, untyped, prs, cmd })
        {
            Release(p);
        }
    }

    /// <summary>r-ado-1b/rs-open-by-properties, r-str-ado-act/ado-stream, r-q-ado-misc/stream-file-ops, record-open-forms.</summary>
    private static void RecordsetStreamRecord()
    {
        Guid iidRecordset = new("00001556-0000-0010-8000-00AA006D2EA4");
        nint rs = New(OwnRecordset);
        Assert.Equal(0, Get(rs, "State"));
        Assert.Equal(0, Get(rs, "CursorType"));
        Assert.Equal(1, Get(rs, "LockType"));
        Assert.Equal(2, Get(rs, "CursorLocation"));
        Assert.Equal(0, Get(rs, "MaxRecords"));
        Assert.Null(Put(rs, "CursorType", 1).Failure);
        Assert.Null(Put(rs, "LockType", 3).Failure);
        Assert.Null(Put(rs, "Source", "SELECT ID, S FROM T ORDER BY ID").Failure);
        Assert.Equal(1, Get(rs, "CursorType"));
        Assert.Equal("SELECT ID, S FROM T ORDER BY ID", Get(rs, "Source"));
        foreach (string member in new[] { "EOF", "BOF", "RecordCount", "AbsolutePosition" })
        {
            AssertAdoError(Invoke(rs, member, 2), ObjectClosed, "ADODB.Recordset", 1240653, iidRecordset);
        }
        foreach (string member in new[] { "MoveFirst", "MoveNext", "Close" })
        {
            AssertAdoError(Call(rs, member), ObjectClosed, "ADODB.Recordset", 1240653, iidRecordset);
        }
        Release(rs);

        Guid iidStream = new("00001565-0000-0010-8000-00AA006D2EA4");
        nint st = New(OwnStream);
        Assert.Equal(0, Get(st, "State"));
        Assert.Equal(2, Get(st, "Type"));
        Assert.Equal("Unicode", Get(st, "Charset"));
        Assert.Equal(0, Get(st, "Mode"));
        Assert.Null(Put(st, "Type", 1).Failure);
        Assert.Equal(1, Get(st, "Type"));
        AssertAdoError(Invoke(st, "Size", 2), ObjectClosed, "ADODB.Stream", 1240653, iidStream);
        AssertAdoError(Call(st, "ReadText", -1), ObjectClosed, "ADODB.Stream", 1240653, iidStream);
        AssertAdoError(Call(st, "Close"), ObjectClosed, "ADODB.Stream", 1240653, iidStream);
        Release(st);

        Guid iidRecord = new("00001562-0000-0010-8000-00AA006D2EA4");
        nint rec = New(OwnRecord);
        Assert.Equal(0, Get(rec, "State"));
        AssertAdoError(Invoke(rec, "RecordType", 2), ObjectClosed, "ADODB.Record", 1240653, iidRecord);
        AssertAdoError(Invoke(rec, "ParentURL", 2), ObjectClosed, "ADODB.Record", 1240653, iidRecord);
        Release(rec);
    }

    // ---- the caller side: raw IDispatch ----

    private static readonly object Missing = new();

    /// <summary>An object argument (VT_DISPATCH, borrowed).</summary>
    private sealed record Dispatch(nint Pointer);

    private readonly record struct Error(int Scode, string Source, string Description, string? HelpFile, uint HelpContext, Guid InfoGuid);

    private readonly record struct Outcome(object? Value, Error? Failure);

    private static nint New(Guid clsid)
    {
        Assert.Equal(0, Create(clsid, 0, ClsctxInprocServer, IidIDispatch, out nint created));
        return created;
    }

    private static object? Get(nint target, string member, params object[] args)
    {
        Outcome o = Invoke(target, member, 2, args);
        Assert.True(o.Failure is null, $"{member}: {o.Failure}");
        return o.Value;
    }

    private static Outcome Call(nint target, string member, params object[] args) => Invoke(target, member, 1, args);

    private static Outcome Put(nint target, string member, object value) => Invoke(target, member, 4, value);

    private static Outcome Invoke(nint target, string member, ushort flags, params object[] args)
    {
        Assert.Equal(0, GetIds(target, member, out int id));
        NativeVariant* cells = stackalloc NativeVariant[Math.Max(args.Length, 1)];
        for (int i = 0; i < args.Length; i++)
        {
            cells[args.Length - 1 - i] = Cell(args[i]);
        }
        int putId = -3;
        DispParams dp = new() { Args = (nint)cells, ArgCount = (uint)args.Length };
        if (flags == 4)
        {
            dp.NamedArgs = (nint)(&putId);
            dp.NamedArgCount = 1;
        }
        NativeVariant result = default;
        ExcepInfo info = default;
        uint argError = 0;
        Guid none = Guid.Empty;
        int hr = ((delegate* unmanaged<nint, int, Guid*, uint, ushort, DispParams*, NativeVariant*, ExcepInfo*, uint*, int>)Vtable(target)[6])(target, id, &none, 0x0409, flags, &dp, &result, &info, &argError);
        for (int i = 0; i < args.Length; i++)
        {
            if ((VarType)cells[i].Vt == VarType.Bstr)
            {
                Marshal.FreeBSTR(cells[i].Pointer);
            }
        }
        if (hr == HResults.DISP_E_EXCEPTION)
        {
            Assert.Equal(0, result.Vt); // a failure leaves no result
            Guid infoGuid = Guid.Empty;
            if (GetErrorInfo(0, out nint errorInfo) == 0 && errorInfo != 0)
            {
                Assert.Equal(0, ((delegate* unmanaged<nint, Guid*, int>)Vtable(errorInfo)[3])(errorInfo, &infoGuid));
                Release(errorInfo);
            }
            var error = new Error(info.Scode, Marshal.PtrToStringBSTR(info.Source), Marshal.PtrToStringBSTR(info.Description),
                info.HelpFile == 0 ? null : Marshal.PtrToStringBSTR(info.HelpFile), info.HelpContext, infoGuid);
            Marshal.FreeBSTR(info.Source);
            Marshal.FreeBSTR(info.Description);
            Marshal.FreeBSTR(info.HelpFile);
            return new Outcome(null, error);
        }
        Assert.True(hr == 0, $"{member}: 0x{hr:X8}");
        return new Outcome(Value(result), null);
    }

    private static NativeVariant Cell(object value)
    {
        NativeVariant v = default;
        switch (value)
        {
            case int i:
                v.Vt = (ushort)VarType.I4;
                v.Int64 = (uint)i;
                break;
            case string s:
                v.Vt = (ushort)VarType.Bstr;
                v.Pointer = Marshal.StringToBSTR(s);
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

    /// <summary>A result as a managed value; an object result is the owned interface pointer (released by the caller).</summary>
    private static object? Value(NativeVariant v)
    {
        switch ((VarType)v.Vt)
        {
            case VarType.Empty:
                return null;
            case VarType.I4:
                return (int)v.Int64;
            case VarType.I2:
                return (short)v.Int64;
            case VarType.I8:
                return v.Int64;
            case VarType.UI1:
                return (byte)v.Int64;
            case VarType.Bool:
                return (short)v.Int64 != 0;
            case VarType.Bstr when v.Pointer == 0:
                return null; // a null BSTR
            case VarType.Bstr:
                string s = Marshal.PtrToStringBSTR(v.Pointer);
                Marshal.FreeBSTR(v.Pointer);
                return s;
            case VarType.Dispatch:
                return v.Pointer;
            default:
                throw new InvalidOperationException("unexpected result type " + (VarType)v.Vt);
        }
    }

    private static void AssertAdoError(Outcome outcome, int scode, string source, uint helpContext, Guid iid, string? description = null)
    {
        Assert.True(outcome.Failure is not null, "expected a failure");
        Error e = outcome.Failure.Value;
        Assert.Equal(scode, e.Scode);
        Assert.Equal(source, e.Source);
        Assert.Equal(@"C:\WINDOWS\HELP\ADO270.CHM", e.HelpFile);
        Assert.Equal(helpContext, e.HelpContext);
        Assert.Equal(iid, e.InfoGuid);
        if (description is not null)
        {
            Assert.Equal(description, e.Description);
        }
    }

    /// <summary>NotModelled.AdoProvider (II-12 §15): a named refusal, never an ACE HRESULT or a native match.</summary>
    private static void AssertRefused(Outcome outcome)
    {
        Assert.True(outcome.Failure is not null, "expected the provider refusal");
        Assert.Equal(HResults.E_NOTIMPL, outcome.Failure.Value.Scode);
        Assert.Equal("CalcDb", outcome.Failure.Value.Source);
        Assert.StartsWith("NotModelled.AdoProvider: ", outcome.Failure.Value.Description, StringComparison.Ordinal);
        Assert.Contains("'MSDASQL'", outcome.Failure.Value.Description, StringComparison.Ordinal);
    }

    private static nint TypeAttr(nint typeInfo, out nint attr)
    {
        nint a = 0;
        Assert.Equal(0, ((delegate* unmanaged<nint, nint*, int>)Vtable(typeInfo)[3])(typeInfo, &a));
        attr = a;
        return a;
    }
}
