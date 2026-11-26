using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;
using Xunit;

namespace CalcDb.Com.Tests;

/// <summary>
/// Targeted tests for r-q53 Part B (DAO <c>AbsolutePosition</c> get/put edge cases and derived
/// <c>Filter</c>/<c>Sort</c> -&gt; <c>OpenRecordset</c> across ACCDB and Jet 4 MDB) and <c>w8-dao-sql-meta</c>
/// (Decimal aggregate <c>Field.Attributes</c>/<c>Properties("Precision"/"Scale")</c>, scalar subquery
/// <c>Field.Properties</c>, and overlay <c>PageAllocator.ExtentOf</c> in domain functions inside a transaction).
/// </summary>
public sealed unsafe class DaoAbsPosFilterSortTests : IDisposable
{
    private const string DataAccdb = "ff6d04bc81ecd3e6c4cea8592a77c5ff17b6856fe0f6308be5d2f9ff32573767";
    private const string DataMdb = "19ba890b854f82dd9aff75bfb5bb217a521d0252e3076073b7acf3bd8ed41cb2";

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_7E90, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                Method(_databases[i], "Close");
            }
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            _held[i].Obj.Release();
        }
        foreach (OwnedVariant owned in _ownedVariants)
        {
            owned.Dispose();
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if (v->Vt == (ushort)VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free(v);
        }
        foreach (string path in _files)
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".laccdb")); } catch { }
            try { File.Delete(Path.ChangeExtension(path, ".ldb")); } catch { }
        }
    }

    private DatabaseObject OpenFixture(string sha, string ext, bool readOnly)
    {
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w9-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, CalcDb.Evidence.ArtifactStore.ForRepository(DeclarationGenerationTests.Root()).ReadAll(sha));
        _files.Add(path);
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(path), Arg(false), Arg(readOnly)])));
        _databases.Add(db);
        return db;
    }

    [Theory]
    [InlineData(DataAccdb, ".accdb")]
    [InlineData(DataMdb, ".mdb")]
    public void AbsolutePosition_get_put_bounds_coercion_partial_and_types(string sha, string ext)
    {
        DatabaseObject db = OpenFixture(sha, ext, readOnly: true);

        // Dynaset (2): full population bounds, clamping, coercion, BOF/EOF recovery.
        RecordsetObject rsD = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 2)));
        Ok(Method(rsD, "MoveLast"));
        Assert.Equal(8, Int32Value(Ok(Get(rsD, "RecordCount"))));
        Assert.Equal(7, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));

        // Clamp RecordCount -> RecordCount - 1 (8 -> 7).
        Ok(Put(rsD, "AbsolutePosition", 8));
        Assert.Equal(7, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));
        Assert.Equal(8, Int32Value(Ok(FieldValue(rsD, "ID"))));

        // Coercion via Invoke: "5" -> 5, 1.6 -> 2, Empty -> 0.
        InvokeOk(rsD, "AbsolutePosition", InvokeKind.PropertyPut, "5");
        Assert.Equal(5, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));
        InvokeOk(rsD, "AbsolutePosition", InvokeKind.PropertyPut, 1.6);
        Assert.Equal(2, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));
        InvokeOk(rsD, "AbsolutePosition", InvokeKind.PropertyPut, (object?)null);
        Assert.Equal(0, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));

        // Out-of-range (<0 and >RecordCount) -> 3001 without IErrorInfo; position unchanged.
        AssertDaoError(Put(rsD, "AbsolutePosition", -1), 3001, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsD, "AbsolutePosition", 100), 3001, "DAO.Recordset", errorInfo: false);
        Assert.Equal(0, Int32Value(Ok(Get(rsD, "AbsolutePosition"))));

        // Null and non-numeric BSTR -> 3421 WITH IErrorInfo (via IDispatch Invoke coercion).
        InvokeDaoError(rsD, "AbsolutePosition", InvokeKind.PropertyPut, 3421, "DAO.Recordset", DBNull.Value);
        InvokeDaoError(rsD, "AbsolutePosition", InvokeKind.PropertyPut, 3421, "DAO.Recordset", "abc");

        // Partial population does NOT populate forward (abspos-get-put-read calls 321, 341, 366, 386).
        RecordsetObject rsPart = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 2)));
        Assert.Equal(1, Int32Value(Ok(Get(rsPart, "RecordCount"))));
        AssertDaoError(Put(rsPart, "AbsolutePosition", 5), 3001, "DAO.Recordset", errorInfo: false);
        Assert.Equal(1, Int32Value(Ok(Get(rsPart, "RecordCount"))));
        Assert.Equal(0, Int32Value(Ok(Get(rsPart, "AbsolutePosition"))));
        Ok(Method(rsPart, "Close"));

        // SELECT snapshot (4): AbsolutePosition get/put from EOF, bounds.
        SelectRecordsetObject rsQ = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, Cat, N FROM Items ORDER BY ID", 4)));
        Assert.Equal(0, Int32Value(Ok(Get(rsQ, "AbsolutePosition"))));
        Assert.Equal(8, Int32Value(Ok(Get(rsQ, "RecordCount"))));
        Ok(Method(rsQ, "MoveLast"));
        Ok(Method(rsQ, "MoveNext"));
        Assert.Equal(-1, Int32Value(Ok(Get(rsQ, "AbsolutePosition"))));
        Ok(Put(rsQ, "AbsolutePosition", 2));
        Assert.Equal(2, Int32Value(Ok(Get(rsQ, "AbsolutePosition"))));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rsQ, "ID"))));
        AssertDaoError(Put(rsQ, "AbsolutePosition", -1), 3001, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsQ, "AbsolutePosition", 50), 3001, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsQ, "Close"));

        // Empty table: get -> -1, put 0 -> 3021, put -1 -> 3001.
        RecordsetObject rsE = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "EmptyTbl", 2)));
        Assert.Equal(-1, Int32Value(Ok(Get(rsE, "AbsolutePosition"))));
        AssertDaoError(Put(rsE, "AbsolutePosition", 0), 3021, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsE, "AbsolutePosition", -1), 3001, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsE, "Close"));

        // Table-type (1) -> 3251; Forward-only (8) -> 3219.
        RecordsetObject rsT = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 1)));
        AssertDaoError(Get(rsT, "AbsolutePosition"), 3251, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsT, "AbsolutePosition", 0), 3251, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsT, "Close"));

        RecordsetObject rsF = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 8)));
        AssertDaoError(Get(rsF, "AbsolutePosition"), 3219, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Put(rsF, "AbsolutePosition", 0), 3219, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsF, "Close"));

        Ok(Method(rsD, "Close"));
    }

    [Theory]
    [InlineData(DataAccdb, ".accdb")]
    [InlineData(DataMdb, ".mdb")]
    public void AbsolutePosition_delete_bookmark_and_pending_edit_cancel(string sha, string ext)
    {
        DatabaseObject db = OpenFixture(sha, ext, readOnly: false);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 2)));
        RecordsetObject cl = Recover<RecordsetObject>(Ok(Method(rs, "Clone")));
        Ok(Method(rs, "MoveLast"));
        Ok(Method(rs, "MoveFirst"));

        // Own Delete -> AbsolutePosition = -1.
        Ok(Put(rs, "AbsolutePosition", 1));
        Ok(Method(rs, "Delete"));
        Assert.Equal(-1, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
        Assert.Equal(7, Int32Value(Ok(Get(rs, "RecordCount"))));

        // Sibling delete of last populated row -> keeps ordinal 6 at RecordCount 6.
        Ok(Method(rs, "MoveLast"));
        Assert.Equal(6, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
        Ok(Method(cl, "MoveLast"));
        Ok(Method(cl, "Delete"));
        Assert.Equal(6, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
        Assert.Equal(6, Int32Value(Ok(Get(rs, "RecordCount"))));

        // Bookmark after deletes preserves original keyset slot; ffffffff on DeletedBySibling -> 3001, keeps ordinal.
        Ok(Put(rs, "AbsolutePosition", 1));
        Assert.Equal("02020000", Convert.ToHexStringLower(BytesValue(Ok(Get(rs, "Bookmark")))));
        Ok(Put(cl, "AbsolutePosition", 1));
        Ok(Method(cl, "Delete"));
        AssertDaoError(Put(rs, "Bookmark", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }), 3001, "DAO.Recordset", errorInfo: false);
        Assert.Equal(1, Int32Value(Ok(Get(rs, "AbsolutePosition"))));

        // Pending Edit and AddNew are cancelled by valid AbsolutePosition put.
        Ok(Put(rs, "AbsolutePosition", 0));
        Ok(Method(rs, "Edit"));
        Ok(PutFieldValue(rs, "N", 777));
        Ok(Put(rs, "AbsolutePosition", 1));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));
        Ok(Put(rs, "AbsolutePosition", 0));
        Assert.Equal(15, Int32Value(Ok(FieldValue(rs, "N"))));

        Ok(Method(rs, "AddNew"));
        Ok(PutFieldValue(rs, "ID", 90));
        Ok(Put(rs, "AbsolutePosition", 0));
        Assert.Equal((short)0, Int16Value(Ok(Get(rs, "EditMode"))));

        // AddNew + Update gets monotonic keyset slot 08020000.
        Ok(Method(rs, "AddNew"));
        Ok(PutFieldValue(rs, "ID", 91));
        Ok(PutFieldValue(rs, "N", 910));
        Ok(Method(rs, "Update"));
        Assert.Equal("08020000", Convert.ToHexStringLower(BytesValue(Ok(Get(rs, "LastModified")))));

        Ok(Method(cl, "Close"));
        Ok(Method(rs, "Close"));
    }

    [Theory]
    [InlineData(DataAccdb, ".accdb")]
    [InlineData(DataMdb, ".mdb")]
    public void Derived_Filter_Sort_OpenRecordset_read_chain_clone_edit_and_errors(string sha, string ext)
    {
        DatabaseObject db = OpenFixture(sha, ext, readOnly: false);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 2)));
        Ok(Put(rs, "Filter", "Cat = 'A'"));
        Ok(Put(rs, "Sort", "ID DESC"));
        SelectRecordsetObject d = Recover<SelectRecordsetObject>(Ok(Method(rs, "OpenRecordset")));
        Assert.Equal((short)2, Int16Value(Ok(Get(d, "Type"))));
        Assert.Equal("", TextValue(Ok(Get(d, "Filter"))));
        Assert.Equal("", TextValue(Ok(Get(d, "Sort"))));
        Assert.True(BoolValue(Ok(Get(d, "Updatable"))));
        Assert.Equal(3, Int32Value(Ok(Get(d, "RecordCount"))));

        // Chained derivation combines parent filter + new filter.
        Ok(Put(d, "Filter", "N >= 15"));
        Ok(Put(d, "Sort", "ID ASC"));
        SelectRecordsetObject dSub = Recover<SelectRecordsetObject>(Ok(Method(d, "OpenRecordset")));
        Assert.Equal(2, Int32Value(Ok(Get(dSub, "RecordCount"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(dSub, "ID"))));
        Ok(Method(dSub, "Close"));

        // Clone survives parent Close().
        SelectRecordsetObject dClone = Recover<SelectRecordsetObject>(Ok(Method(d, "Clone")));
        Assert.Equal(3, Int32Value(Ok(Get(dClone, "RecordCount"))));

        // Edit + AddNew (matching and non-matching filter rows both appear in subsequent walk) + FindFirst + Delete.
        Ok(Method(d, "Edit"));
        Ok(PutFieldValue(d, "N", 120));
        Ok(Method(d, "Update"));

        Ok(Method(d, "AddNew"));
        Ok(PutFieldValue(d, "ID", 101));
        Ok(PutFieldValue(d, "Cat", "A"));
        Ok(PutFieldValue(d, "N", 1010));
        Ok(Method(d, "Update"));

        Ok(Method(d, "AddNew"));
        Ok(PutFieldValue(d, "ID", 102));
        Ok(PutFieldValue(d, "Cat", "Z"));
        Ok(PutFieldValue(d, "N", 1020));
        Ok(Method(d, "Update"));

        Ok(Method(d, "MoveFirst"));
        var ids = new List<int>();
        while (!BoolValue(Ok(Get(d, "EOF"))))
        {
            ids.Add(Int32Value(Ok(FieldValue(d, "ID"))));
            Ok(Method(d, "MoveNext"));
        }
        Assert.Equal([3, 2, 1, 101, 102], ids);
        Assert.Equal(5, Int32Value(Ok(Get(d, "RecordCount"))));

        Ok(Method(d, "FindFirst", "ID = 2"));
        Assert.False(BoolValue(Ok(Get(d, "NoMatch"))));
        Ok(Method(d, "Delete"));
        Ok(Method(d, "Close"));

        // Errors on OpenRecordset: ForwardOnly 3219, Type=1 -> 3001, bad Filter/Sort -> 3075/3061/3464/3138, dbReadOnly -> 3027, closed -> 3420.
        RecordsetObject rsF = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "Items", 8)));
        Ok(Put(rsF, "Filter", "Cat = 'A'"));
        AssertDaoError(Method(rsF, "OpenRecordset"), 3219, "DAO.Recordset", errorInfo: false);
        Ok(Method(rsF, "Close"));

        AssertDaoError(Method(rs, "OpenRecordset", 1), 3001, "DAO.Recordset", errorInfo: false);

        Ok(Put(rs, "Sort", ""));
        Ok(Put(rs, "Filter", "Cat ="));
        AssertDaoError(Method(rs, "OpenRecordset"), 3075, "DAO.Recordset", errorInfo: false, "Syntax error (missing operator) in query expression 'Cat ='.");

        Ok(Put(rs, "Filter", "NoSuchCol = 1"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3061, "DAO.Recordset", errorInfo: false, "Too few parameters. Expected 2.");

        Ok(Put(rs, "Filter", "N = 'notanum'"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3464, "DAO.Recordset", errorInfo: false, "Data type mismatch in criteria expression.");

        Ok(Put(rs, "Filter", ""));
        Ok(Put(rs, "Sort", "NoSuchCol ASC"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3061, "DAO.Recordset", errorInfo: false, "Too few parameters. Expected 2.");

        Ok(Put(rs, "Sort", "ID BADDIR"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3075, "DAO.Recordset", errorInfo: false, "Syntax error (missing operator) in query expression 'ID BADDIR'.");

        Ok(Put(rs, "Sort", "ORDER BY ID"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3138, "DAO.Recordset", errorInfo: false, "Syntax error in ORDER BY clause.");

        Ok(Put(rs, "Sort", ""));
        SelectRecordsetObject bro = Recover<SelectRecordsetObject>(Ok(Method(rs, "OpenRecordset", 2, 4)));
        Assert.False(BoolValue(Ok(Get(bro, "Updatable"))));
        AssertDaoError(Method(bro, "Edit"), 3027, "DAO.Recordset", errorInfo: false);
        Ok(Method(bro, "Close"));

        Ok(Method(rs, "Close"));
        AssertDaoError(Method(rs, "OpenRecordset"), 3420, "DAO.Recordset", errorInfo: false);

        // Clone survived parent Close().
        Ok(Method(dClone, "MoveFirst"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(dClone, "ID"))));
        Ok(Method(dClone, "Close"));
    }

    [Theory]
    [InlineData(DataAccdb, ".accdb")]
    [InlineData(DataMdb, ".mdb")]
    public void Decimal_query_aggregate_field_attributes_and_precision_scale_properties(string sha, string ext)
    {
        DatabaseObject db = OpenFixture(sha, ext, readOnly: true);

        // Grouped Decimal aggregates: SUM/AVG report Precision=28, Scale=4; MIN/MAX report Precision=18, Scale=4.
        SelectRecordsetObject grp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT Cat, Sum(Amt) AS SAmt, Avg(Amt) AS AAmt, Min(Amt) AS MinAmt, Max(Amt) AS MaxAmt FROM Items GROUP BY Cat ORDER BY Cat", 4)));
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(grp, "Fields")));
        CheckDecimalField(fields, "SAmt", expectedPrecision: 28, expectedScale: 4);
        CheckDecimalField(fields, "AAmt", expectedPrecision: 28, expectedScale: 4);
        CheckDecimalField(fields, "MinAmt", expectedPrecision: 18, expectedScale: 4);
        CheckDecimalField(fields, "MaxAmt", expectedPrecision: 18, expectedScale: 4);
        Ok(Method(grp, "Close"));

        // Ungrouped aggregate snapshot: Precision and Scale fail 3021 ("No current record.").
        SelectRecordsetObject ungrp = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT Sum(Amt) AS SAmt, Avg(Amt) AS AAmt FROM Items", 4)));
        DispatchObject uFields = Recover<DispatchObject>(Ok(Get(ungrp, "Fields")));
        DispatchObject sAmt = Recover<DispatchObject>(Ok(Get(uFields, "Item", "SAmt")));
        Assert.Equal(1, Int32Value(Ok(Get(sAmt, "Attributes"))));
        DispatchObject props = Recover<DispatchObject>(Ok(Get(sAmt, "Properties")));
        DispatchObject pPrec = Recover<DispatchObject>(Ok(Get(props, "Item", "Precision")));
        DispatchObject pScale = Recover<DispatchObject>(Ok(Get(props, "Item", "Scale")));
        AssertDaoError(Get(pPrec, "Value"), 3021, "DAO.Field", errorInfo: false);
        AssertDaoError(Get(pScale, "Value"), 3021, "DAO.Field", errorInfo: false);
        Ok(Method(ungrp, "Close"));
    }

    private void CheckDecimalField(DispatchObject fields, string name, short expectedPrecision, short expectedScale)
    {
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        Assert.Equal((short)20, Int16Value(Ok(Get(field, "Type"))));
        Assert.Equal(1, Int32Value(Ok(Get(field, "Attributes"))));
        DispatchObject props = Recover<DispatchObject>(Ok(Get(field, "Properties")));
        DispatchObject pPrec = Recover<DispatchObject>(Ok(Get(props, "Item", "Precision")));
        DispatchObject pScale = Recover<DispatchObject>(Ok(Get(props, "Item", "Scale")));
        Assert.Equal(expectedPrecision, Int16Value(Ok(Get(pPrec, "Value"))));
        Assert.Equal(expectedScale, Int16Value(Ok(Get(pScale, "Value"))));
    }

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private CallResult PutFieldValue(DispatchObject rs, string name, object? value)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Put(field, "Value", value);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
            case int i:
                v->Vt = (ushort)VarType.I4;
                v->Int64 = (uint)i;
                break;
            case double d:
                v->Vt = (ushort)VarType.R8;
                v->Double = d;
                break;
            case bool b:
                v->Vt = (ushort)VarType.Bool;
                v->Int64 = b ? 0xFFFF : 0;
                break;
            case string s:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(s);
                break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                bytes.CopyTo(new Span<byte>(SafeArrays.ElementPointer(psa, 0), bytes.Length));
                _ownedVariants.Add(OwnedVariant.TakeArray(psa, VarType.UI1));
                v->Vt = (ushort)(VarType.Array | VarType.UI1);
                v->Pointer = psa;
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = [.. args.Select(Arg)];
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);

    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private void InvokeOk(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        int hr = InvokeRaw(target, member, kind, out ExcepInfo excep, args);
        FreeExcep(ref excep);
        Assert.Equal(HResults.S_OK, hr);
    }

    private void InvokeDaoError(DispatchObject target, string member, InvokeKind kind, int expectedNumber, string expectedSource, params object?[] args)
    {
        int hr = InvokeRaw(target, member, kind, out ExcepInfo excep, args);
        try
        {
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(unchecked((int)(0x800A0000u | (uint)expectedNumber)), excep.Scode);
            Assert.Equal(expectedSource, Marshal.PtrToStringBSTR(excep.Source));
        }
        finally
        {
            FreeExcep(ref excep);
        }
    }

    private int InvokeRaw(DispatchObject target, string member, InvokeKind kind, out ExcepInfo excep, params object?[] args)
    {
        excep = default;
        NativeVariant* reversed = args.Length == 0 ? null : (NativeVariant*)NativeMemory.AllocZeroed((nuint)args.Length, (nuint)sizeof(NativeVariant));
        int propPutDispId = -3;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                reversed[args.Length - 1 - i] = *(NativeVariant*)Arg(args[i]);
            }
            DispParams dp = new() { Args = (nint)reversed, ArgCount = (uint)args.Length };
            if (kind == InvokeKind.PropertyPut)
            {
                dp.NamedArgs = (nint)(&propPutDispId);
                dp.NamedArgCount = 1;
            }
            NativeVariant result = default;
            ExcepInfo localExcep = default;
            uint argError = 0;
            int hr = target.Invoke(target.Declaration.Member(member, kind).MemberId, (ushort)kind, &dp, &result, &localExcep, &argError);
            excep = localExcep;
            return hr;
        }
        finally
        {
            if (reversed is not null)
            {
                NativeMemory.Free(reversed);
            }
        }
    }

    private static void FreeExcep(ref ExcepInfo excep)
    {
        if (excep.Source != 0) Marshal.FreeBSTR(excep.Source);
        if (excep.Description != 0) Marshal.FreeBSTR(excep.Description);
        if (excep.HelpFile != 0) Marshal.FreeBSTR(excep.HelpFile);
    }

    private static CallResult Ok(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        return result;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo, string? description = null)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
        if (description is not null)
        {
            Assert.Equal(description, result.Failure.Description);
        }
    }

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4);
        return (int)result.Scalar.Int64;
    }

    private static short Int16Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I2);
        return unchecked((short)result.Scalar.Int64);
    }

    private static bool BoolValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Bool);
        return (short)result.Scalar.Int64 != 0;
    }

    private static string TextValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private static byte[] BytesValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Array | VarType.UI1, (VarType)v.Vt);
        (int lower, int count) = SafeArrays.Shape(v.Pointer)[0];
        byte[] bytes = count <= 0 ? [] : new ReadOnlySpan<byte>(SafeArrays.ElementPointer(v.Pointer, lower), count).ToArray();
        result.ReleaseUntaken();
        return bytes;
    }
}
