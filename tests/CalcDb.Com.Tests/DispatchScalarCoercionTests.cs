using System.Runtime.InteropServices;
using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;
using AdoRecordsetObject = CalcDb.Com.Ado.RecordsetObject;
using DaoRecordsetObject = CalcDb.Com.Dao.RecordsetObject;

namespace CalcDb.Com.Tests;

/// <summary>
/// W-08/W-09 (ptrj): tests IDispatch::Invoke scalar-argument coercion HRESULT propagation
/// (<see cref="ArgumentConversion"/>) and pre-coercion source-<see cref="VarType"/> inspection
/// (<see cref="DispatchObject.ScalarArgumentRefusal"/>) across DAO and ADO dispatch wrappers.
/// </summary>
public sealed unsafe class DispatchScalarCoercionTests : IDisposable
{
    private const string RQ32Accdb = "55b55055cb8bcf929f6a3b29450ed0545dcb638b14d5b2a4134ac9f15588b0d0";

    private readonly HostBinding _binding = new(0xDA0_947F, Standalone: true, 0, 0, 0, 0, 0);
    private readonly EngineState _engine;
    private readonly List<nint> _held = [];
    private readonly List<nint> _cells = [];
    private readonly List<string> _files = [];

    public DispatchScalarCoercionTests()
    {
        _engine = new EngineState(_binding);
    }

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        foreach (nint cell in _cells)
        {
            NativeVariant* v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free(v);
        }
        foreach (string path in _files)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Dao_SelectField_GetChunk_distinguishes_overflow_from_type_mismatch_and_refuses_unrecorded_source_VarTypes()
    {
        DatabaseObject db = OpenFixture(RQ32Accdb, ".accdb");
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo FROM T ORDER BY ID", 4)));
        Ok(Method(rs, "MoveNext"));
        Ok(Method(rs, "MoveNext")); // Row 3 ("abcdefghij")
        SelectFieldObject memo = Recover<SelectFieldObject>(Ok(Get(
            Recover<SelectFieldsObject>(Ok(Get(rs, "Fields"))), "Item", "FMemo")));

        // q3-lv-getchunk-args-accdb seq 29 / q3-lv-getchunk-args-mdb seq 21: DISP_E_OVERFLOW -> DAO 3001 + IErrorInfo + *puArgErr.
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
            RawInt32(0), RawDouble(4294967296.0));
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
            RawDouble(-4294967296.0), RawInt32(4));

        // DISP_E_TYPEMISMATCH -> DAO 3421 + IErrorInfo + *puArgErr.
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
            RawNull(), RawInt32(4));
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
            RawInt32(0), RawBstr("abc"));

        // Unrecorded source VarTypes refused by name before VariantChangeTypeEx runs.
        int byRefI4Value = 4;
        NativeVariant innerI4 = RawInt32(4);
        nint bstrVal = Marshal.StringToBSTR("2");
        try
        {
            NativeVariant[] unrecorded =
            [
                RawSingle(1.5f),
                RawCurrency(40000),
                RawDate(45000.0),
                RawDecimal(4m),
                RawInt64(4L),
                RawByRef(VarType.I4, (nint)(&byRefI4Value)),
                RawByRef(VarType.Bstr, (nint)(&bstrVal)),
                RawByRef(VarType.Variant, (nint)(&innerI4)),
            ];
            foreach (NativeVariant bad in unrecorded)
            {
                AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", bad, RawInt32(4));
                AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), bad);
            }
        }
        finally
        {
            Marshal.FreeBSTR(bstrVal);
        }

        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_Recordset_scalar_members_refuse_unrecorded_source_VarTypes_and_keep_recorded_errors()
    {
        DatabaseObject db = OpenFixture(RQ32Accdb, ".accdb");
        DaoRecordsetObject rs = Recover<DaoRecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 2)));

        int byRefI4 = 1;
        float byRefR4 = 50f;
        nint byRefBstr = Marshal.StringToBSTR("ID = 1");
        try
        {
            // Move (r-cursor-1b/move-argument-forms): unrecorded VarTypes refused; Null -> 3421, Missing -> 3001.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(1.0f),
                RawCurrency(10000),
                RawDate(1.0),
                RawDecimal(1m),
                RawInt64(1L),
                RawByRef(VarType.I4, (nint)(&byRefI4)),
            })
            {
                AssertInvokeNotModelled(rs, "Move", InvokeKind.Method, "NotModelled.MoveArgumentType", bad);
            }
            AssertInvokeDaoError(rs, "Move", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());
            AssertInvokeDaoError(rs, "Move", InvokeKind.Method, 3001, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawMissing());

            // PercentPosition put (r-cursor-1b/percent-assign): unrecorded VarTypes refused; Null -> 3421.
            foreach (NativeVariant bad in new[]
            {
                RawInt16(50),
                RawCurrency(500000),
                RawDate(50.0),
                RawInt64(50L),
                RawByRef(VarType.R4, (nint)(&byRefR4)),
            })
            {
                AssertInvokeNotModelled(rs, "PercentPosition", InvokeKind.PropertyPut, "NotModelled.PercentPositionArgumentType", bad);
            }
            AssertInvokeDaoError(rs, "PercentPosition", InvokeKind.PropertyPut, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // Seek p0 (r-cursor-1b/seek-operator-spelling, r-wire/opt-seek): unrecorded VarTypes refused; Null -> 3421, Missing -> 3001.
            foreach (NativeVariant bad in new[]
            {
                RawDouble(1.0),
                RawDate(1.0),
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "Seek", InvokeKind.Method, "NotModelled.SeekComparisonType", bad, RawInt32(1));
            }
            AssertInvokeDaoError(rs, "Seek", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 1u, RawNull(), RawInt32(1));
            AssertInvokeDaoError(rs, "Seek", InvokeKind.Method, 3001, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 1u, RawMissing(), RawInt32(1));

            // FindFirst p0 (r-cursor-1b/find-expression-forms): unrecorded VarTypes refused; Null -> 3421.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(1.0f),
                RawDate(1.0),
                RawDecimal(1m),
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "FindFirst", InvokeKind.Method, "NotModelled.FindCriteriaType", bad);
            }
            AssertInvokeDaoError(rs, "FindFirst", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());
        }
        finally
        {
            Marshal.FreeBSTR(byRefBstr);
        }

        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_Workspace_CommitTrans_and_DBEngine_CreateDatabase_refuse_unrecorded_source_VarTypes_and_keep_recorded_errors()
    {
        WorkspaceObject ws = _engine.DefaultWorkspace;
        nint enginePtr = 0;
        Assert.Equal(HResults.S_OK, DBEngineObject.Create(_binding, 0, ComIids.IDispatch, &enginePtr));
        _held.Add(enginePtr);
        DBEngineObject engineObj = ComObject.Recover<DBEngineObject>(enginePtr);

        int byRefI4 = 0;
        nint byRefBstr = Marshal.StringToBSTR("temp.accdb");
        try
        {
            // Workspace.CommitTrans (r-txn-1b/commit-option-vartypes): unrecorded VarTypes refused; Null -> 3421.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(0f),
                RawCurrency(0),
                RawDate(0.0),
                RawDecimal(0m),
                RawInt64(0L),
                RawByRef(VarType.I4, (nint)(&byRefI4)),
            })
            {
                AssertInvokeNotModelled(ws, "CommitTrans", InvokeKind.Method, "NotModelled.CommitTransArgumentType", bad);
            }
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3421, "DAO.Workspace", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // DBEngine.CreateDatabase (r-wire/opt-createdatabase): unrecorded p0/p1 VarTypes refused; p1 Null -> 3421.
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawInt32(1), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawInt32(1));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawDouble(1.0));
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3421, "DAO.DbEngine", publishesErrorInfo: true, expectedArgErr: 1u,
                RawBstr("dummy.accdb"), RawNull(), RawMissing());
        }
        finally
        {
            Marshal.FreeBSTR(byRefBstr);
        }
    }

    [Fact]
    public void Ado_scalar_members_refuse_unrecorded_source_VarTypes_and_keep_recorded_provider_errors()
    {
        nint connPtr = 0;
        nint rsPtr = 0;
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(_binding, 0, ComIids.IDispatch, &connPtr));
        _held.Add(connPtr);
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_binding, 0, ComIids.IDispatch, &rsPtr));
        _held.Add(rsPtr);
        ConnectionObject conn = ComObject.Recover<ConnectionObject>(connPtr);
        AdoRecordsetObject rs = ComObject.Recover<AdoRecordsetObject>(rsPtr);

        // Connection.Open (r-ado-1b/conn-open-args seq 111): Open(Missing, Null, Null) on ACE Provider -> Provider 0x80020005, argErr 0.
        Ok(Call(conn, "Provider", InvokeKind.PropertyPut, "Microsoft.ACE.OLEDB.12.0"));
        AssertInvokeAdoError(conn, "Open", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
            RawMissing(), RawNull(), RawNull());

        string path = CopyFixture(RQ32Accdb, ".accdb");
        Ok(Method(conn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};"));
        Ok(Call(rs, "CursorLocation", InvokeKind.PropertyPut, 3)); // adUseClient
        Ok(Method(rs, "Open", "SELECT ID, FMemo FROM T ORDER BY ID", new DispatchPtr(connPtr), 3, 1));

        int byRefI4 = 0;
        nint byRefBstr = Marshal.StringToBSTR("ID ASC");
        try
        {
            // Recordset.Sort put (r-ado-1b/view-sort-grammar, view-sort-server): only by-value Bstr and Null are recorded.
            foreach (NativeVariant bad in new[]
            {
                RawInt32(1),
                RawDouble(1.0),
                RawDate(1.0),
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "Sort", InvokeKind.PropertyPut, "NotModelled.AdoSortArgumentType", bad);
            }
            AssertInvokeAdoError(rs, "Sort", InvokeKind.PropertyPut, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u, RawNull());

            // Recordset.Find (r-ado-1b/view-find-client): p0 only {Bstr, Null}, p1 only {I4}.
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawInt32(1));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawByRef(VarType.Bstr, (nint)(&byRefBstr)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawDouble(1.0));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawBstr("1"));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeAdoError(rs, "Find", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u, RawNull());
        }
        finally
        {
            Marshal.FreeBSTR(byRefBstr);
        }

        // AdoFieldObject.GetChunk (r-oledb-odbc/stream-getchunk): only by-value VT_I4 is recorded.
        AdoFieldsObject fields = Recover<AdoFieldsObject>(Ok(Get(rs, "Fields")));
        AdoFieldObject field = Recover<AdoFieldObject>(Ok(Get(fields, "Item", "FMemo")));
        int byRefLen = 4;
        AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawInt16(4));
        AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawDouble(4.0));
        AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawByRef(VarType.I4, (nint)(&byRefLen)));
        Ok(Method(rs, "Close"));
        Ok(Method(conn, "Close"));
    }

    private readonly record struct DispatchPtr(nint Pointer);

    private string CopyFixture(string hash, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-dispatch-ptrj-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(hash));
        _files.Add(path);
        return path;
    }

    private DatabaseObject OpenFixture(string hash, string extension)
    {
        string path = CopyFixture(hash, extension);
        return Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(true)])));
    }

    private static NativeVariant RawMissing() => new() { Vt = (ushort)VarType.Error, Int64 = unchecked((uint)HResults.DISP_E_PARAMNOTFOUND) };

    private static NativeVariant RawNull() => new() { Vt = (ushort)VarType.Null };

    private static NativeVariant RawInt16(short value) => new() { Vt = (ushort)VarType.I2, Int64 = (ushort)value };

    private static NativeVariant RawInt32(int value) => new() { Vt = (ushort)VarType.I4, Int64 = (uint)value };

    private static NativeVariant RawInt64(long value) => new() { Vt = (ushort)VarType.I8, Int64 = value };

    private static NativeVariant RawSingle(float value)
    {
        NativeVariant v = new() { Vt = (ushort)VarType.R4 };
        *(float*)&v.Int64 = value;
        return v;
    }

    private static NativeVariant RawDouble(double value) => new() { Vt = (ushort)VarType.R8, Double = value };

    private static NativeVariant RawCurrency(long scaled) => new() { Vt = (ushort)VarType.Currency, Int64 = scaled };

    private static NativeVariant RawDate(double oaDate) => new() { Vt = (ushort)VarType.Date, Double = oaDate };

    private static NativeVariant RawDecimal(decimal value)
    {
        int[] bits = decimal.GetBits(value);
        bool neg = (bits[3] & unchecked((int)0x80000000)) != 0;
        byte scale = (byte)((bits[3] >> 16) & 0xFF);
        ulong lo64 = (uint)bits[0] | ((ulong)(uint)bits[1] << 32);
        using OwnedVariant owned = OwnedVariant.FromDecimal(new DecimalValue(scale, neg, (uint)bits[2], lo64));
        return *owned.Pointer;
    }

    private static NativeVariant RawByRef(VarType inner, nint pointer) =>
        new() { Vt = (ushort)(VarType.ByRef | inner), Pointer = pointer };

    private NativeVariant RawBstr(string text)
    {
        var cell = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        cell->Vt = (ushort)VarType.Bstr;
        cell->Pointer = Marshal.StringToBSTR(text);
        _cells.Add((nint)cell);
        return *cell;
    }

    private static void AssertInvokeDaoError(
        DispatchObject target,
        string member,
        InvokeKind kind,
        int number,
        string source,
        bool publishesErrorInfo,
        uint expectedArgErr,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, uint argError, bool hasErrorInfo) = InvokeRaw(target, member, kind, args);
        try
        {
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(unchecked((int)(0x800A0000u | (uint)number)), excep.Scode);
            Assert.Equal(source, Marshal.PtrToStringBSTR(excep.Source));
            Assert.Equal(expectedArgErr, argError);
            Assert.Equal(publishesErrorInfo, hasErrorInfo);
        }
        finally
        {
            FreeExcep(excep);
        }
    }

    private static void AssertInvokeAdoError(
        DispatchObject target,
        string member,
        InvokeKind kind,
        int expectedScode,
        uint expectedArgErr,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, uint argError, _) = InvokeRaw(target, member, kind, args);
        try
        {
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(expectedScode, excep.Scode);
            Assert.Equal(expectedArgErr, argError);
        }
        finally
        {
            FreeExcep(excep);
        }
    }

    private static void AssertInvokeNotModelled(
        DispatchObject target,
        string member,
        InvokeKind kind,
        string expectedName,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, _, _) = InvokeRaw(target, member, kind, args);
        try
        {
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(HResults.E_NOTIMPL, excep.Scode);
            Assert.StartsWith(expectedName + ":", Marshal.PtrToStringBSTR(excep.Description));
        }
        finally
        {
            FreeExcep(excep);
        }
    }

    private static (int Hr, ExcepInfo Excep, uint ArgError, bool HasErrorInfo) InvokeRaw(
        DispatchObject target,
        string member,
        InvokeKind kind,
        NativeVariant[] args)
    {
        if (NativeRoute.GetErrorInfo(0, out nint stale) == 0 && stale != 0)
        {
            Marshal.Release(stale);
        }
        NativeVariant* reversed = args.Length == 0 ? null : (NativeVariant*)NativeMemory.Alloc((nuint)(args.Length * sizeof(NativeVariant)));
        int propPutId = -3;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                reversed[args.Length - 1 - i] = args[i];
            }
            DispParams dp = new()
            {
                Args = (nint)reversed,
                ArgCount = (uint)args.Length,
                NamedArgs = kind == InvokeKind.PropertyPut ? (nint)(&propPutId) : 0,
                NamedArgCount = kind == InvokeKind.PropertyPut ? 1u : 0u,
            };
            NativeVariant result = default;
            ExcepInfo excep = default;
            uint argError = 0;
            int hr = target.Invoke(target.Declaration.Member(member, kind).MemberId, (ushort)kind, &dp, &result, &excep, &argError);
            bool hasErrorInfo = NativeRoute.GetErrorInfo(0, out nint errorInfo) == 0 && errorInfo != 0;
            if (hasErrorInfo)
            {
                Marshal.Release(errorInfo);
            }
            return (hr, excep, argError, hasErrorInfo);
        }
        finally
        {
            if (reversed is not null)
            {
                NativeMemory.Free(reversed);
            }
        }
    }

    private static void FreeExcep(ExcepInfo excep)
    {
        if (excep.Source != 0)
        {
            Marshal.FreeBSTR(excep.Source);
        }
        if (excep.Description != 0)
        {
            Marshal.FreeBSTR(excep.Description);
        }
        if (excep.HelpFile != 0)
        {
            Marshal.FreeBSTR(excep.HelpFile);
        }
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
                break;
            case DispatchPtr ptr:
                v->Vt = (ushort)VarType.Dispatch;
                v->Pointer = ptr.Pointer;
                break;
            default:
                throw new NotSupportedException(value?.GetType().Name ?? "null");
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        MemberDecl decl = target.Declaration.Member(member, kind);
        nint[] slots = new nint[decl.Parameters.Count];
        for (int i = 0; i < args.Length; i++)
        {
            slots[i] = Arg(args[i]);
        }
        return target.Run(decl, slots);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        _held.Add(result.Scalar.Pointer);
        return ComObject.Recover<T>(result.Scalar.Pointer);
    }
}
