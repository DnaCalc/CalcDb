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
    private const string RQ2CursorAccdb = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2";

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

        // q3-lv-getchunk-args-accdb seq 29 / q3-lv-getchunk-args-mdb seq 21: Bytes (p1) DISP_E_OVERFLOW -> DAO 3001 + IErrorInfo + *puArgErr=0.
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
            RawInt32(0), RawDouble(4294967296.0));
        // Offset (p0) overflow is not recorded and is refused by name.
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentConversion",
            RawDouble(-4294967296.0), RawInt32(4));

        // DISP_E_TYPEMISMATCH -> DAO 3421 + IErrorInfo + *puArgErr.
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
            RawNull(), RawInt32(4));
        AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
            RawInt32(0), RawBstr("abc"));

        // Per-parameter VarType gating (q3-lv-getchunk-args-accdb seq 48-50, -mdb seq 32-34):
        // UI1 is recorded on Bytes (p1) only; I2 and Bool are recorded on Offset (p0) only.
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawByte(1), RawInt32(4));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawInt16(4));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawBool(true));

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

        // SelectRecordsetObject.Move also refuses unrecorded source VarTypes before coercion.
        AssertInvokeNotModelled(rs, "Move", InvokeKind.Method, "NotModelled.MoveArgumentType", RawSingle(1.0f));
        AssertInvokeNotModelled(rs, "Move", InvokeKind.Method, "NotModelled.MoveArgumentType", RawByRef(VarType.I4, (nint)(&byRefI4Value)));

        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_Recordset_scalar_members_refuse_unrecorded_source_VarTypes_and_keep_recorded_errors()
    {
        DatabaseObject db = OpenFixture(RQ32Accdb, ".accdb");
        DaoRecordsetObject rs = Recover<DaoRecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 2)));

        int byRefI4 = 1;
        float byRefR4 = 50f;
        double byRefDate = 1.0;
        double byRefDouble = 1.0;
        NativeVariant innerDate = RawDate(1.0);
        NativeVariant innerDouble = RawDouble(1.0);
        NativeVariant nestedByRefI4 = RawByRef(VarType.I4, (nint)(&byRefI4));
        nint byRefBstr = Marshal.StringToBSTR("ID = 1");
        NativeVariant nestedByRefBstr = RawByRef(VarType.Bstr, (nint)(&byRefBstr));
        try
        {
            // Move (r-cursor-1b/move-argument-forms; ByRef of admitted types: r-q2-cursor-dao/byref-dao-move-seek call 9):
            // unrecorded VarTypes (by value or ByRef), nested ByRef, and unrecorded overflow refused; Null -> 3421, Missing -> 3001.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(1.0f),
                RawCurrency(10000),
                RawDate(1.0),
                RawDecimal(1m),
                RawInt64(1L),
                RawByRef(VarType.Date, (nint)(&byRefDate)),
                RawByRef(VarType.Variant, (nint)(&innerDate)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefI4)),
            })
            {
                AssertInvokeNotModelled(rs, "Move", InvokeKind.Method, "NotModelled.MoveArgumentType", bad);
            }
            AssertInvokeNotModelled(rs, "Move", InvokeKind.Method, "NotModelled.RecordsetArgumentConversion", RawDouble(4294967296.0));
            AssertInvokeDaoError(rs, "Move", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());
            AssertInvokeDaoError(rs, "Move", InvokeKind.Method, 3001, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawMissing());

            // PercentPosition put (r-cursor-1b/percent-assign): unrecorded VarTypes (including ByRef) and unrecorded overflow refused; Null -> 3421.
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
            AssertInvokeNotModelled(rs, "PercentPosition", InvokeKind.PropertyPut, "NotModelled.RecordsetArgumentConversion", RawDouble(1e39));
            AssertInvokeDaoError(rs, "PercentPosition", InvokeKind.PropertyPut, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // Seek p0 (r-cursor-1b/seek-operator-spelling, r-wire/opt-seek; ByRef of admitted types: r-q2-cursor-dao/byref-dao-move-seek call 46):
            // unrecorded VarTypes (by value or ByRef) and nested ByRef refused; Null -> 3421, Missing -> 3001.
            foreach (NativeVariant bad in new[]
            {
                RawDouble(1.0),
                RawDate(1.0),
                RawByRef(VarType.R8, (nint)(&byRefDouble)),
                RawByRef(VarType.Variant, (nint)(&innerDouble)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "Seek", InvokeKind.Method, "NotModelled.SeekComparisonType", bad, RawInt32(1));
            }
            AssertInvokeDaoError(rs, "Seek", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 1u, RawNull(), RawInt32(1));
            AssertInvokeDaoError(rs, "Seek", InvokeKind.Method, 3001, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 1u, RawMissing(), RawInt32(1));

            // FindFirst/FindNext p0 (r-cursor-1b/find-expression-forms; FindFirst ByRef: r-q2-cursor-dao/byref-dao-move-seek call 26):
            // unrecorded VarTypes refused on both; ByRef Bstr still refused on FindNext, and unadmitted/nested ByRef refused on FindFirst.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(1.0f),
                RawDate(1.0),
                RawDecimal(1m),
                RawByRef(VarType.Date, (nint)(&byRefDate)),
                RawByRef(VarType.Variant, (nint)(&innerDate)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "FindFirst", InvokeKind.Method, "NotModelled.FindCriteriaType", bad);
                AssertInvokeNotModelled(rs, "FindNext", InvokeKind.Method, "NotModelled.FindCriteriaType", bad);
            }
            AssertInvokeNotModelled(rs, "FindNext", InvokeKind.Method, "NotModelled.FindCriteriaType", RawByRef(VarType.Bstr, (nint)(&byRefBstr)));
            AssertInvokeDaoError(rs, "FindFirst", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // FindLast/FindPrevious p0 (r-wire/opt-find-criteria seq 47, 50, 53, 56; r-cursor-1b/find-empty-and-ends): only Bstr and Null are recorded.
            foreach (NativeVariant bad in new[]
            {
                RawInt32(1),
                RawDouble(1.0),
                RawBool(true),
                RawEmpty(),
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "FindLast", InvokeKind.Method, "NotModelled.FindCriteriaType", bad);
                AssertInvokeNotModelled(rs, "FindPrevious", InvokeKind.Method, "NotModelled.FindCriteriaType", bad);
            }
            AssertInvokeDaoError(rs, "FindLast", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());
            AssertInvokeDaoError(rs, "FindPrevious", InvokeKind.Method, 3001, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawMissing());
        }
        finally
        {
            Marshal.FreeBSTR(byRefBstr);
        }

        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_Recordset_ByRef_arguments_follow_by_value_rules_for_recorded_Move_GetRows_FindFirst_and_Seek()
    {
        // r-q2-cursor-dao/byref-dao-move-seek: calls 9 (Move ByRef Variant I4 2), 15 (GetRows typed ByRef I4 2),
        // 26 (FindFirst typed ByRef BSTR 'ID = 5'), and 46 (Seek typed ByRef BSTR '=', ByRef Variant I4 3).
        DatabaseObject db = OpenFixture(RQ2CursorAccdb, ".accdb");
        DaoRecordsetObject rs = Recover<DaoRecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 2)));
        DaoRecordsetObject rt = Recover<DaoRecordsetObject>(Ok(Method(db, "OpenRecordset", "T", 1)));
        Ok(Call(rt, "Index", InvokeKind.PropertyPut, "PK_T"));

        NativeVariant moveVar = RawInt32(2);
        int moveTyped = 1;
        int getRowsTyped = 2;
        NativeVariant nullVar = RawNull();
        nint findBstr = Marshal.StringToBSTR("ID = 5");
        nint findVarBstr = Marshal.StringToBSTR("ID = 3");
        NativeVariant findVar = new() { Vt = (ushort)VarType.Bstr, Pointer = findVarBstr };
        nint eqBstr = Marshal.StringToBSTR("=");
        nint geBstr = Marshal.StringToBSTR(">=");
        NativeVariant geVar = new() { Vt = (ushort)VarType.Bstr, Pointer = geBstr };
        NativeVariant seekKeyVar = RawInt32(3);
        int seekKeyTyped = 4;
        NativeVariant nestedKey = RawByRef(VarType.I4, (nint)(&seekKeyTyped));
        try
        {
            // Call 9: rs.Move(ByRef Variant I4 2) lands on ID 3 and leaves caller's Variant unchanged.
            AssertInvokeOk(rs, "Move", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&moveVar)));
            Assert.Equal((ushort)VarType.I4, moveVar.Vt);
            Assert.Equal(2, (int)moveVar.Int64);
            Assert.Equal(3, ReadIntField(rs, "ID"));

            // Call 15: rs.GetRows(typed ByRef I4 2) reads 2 rows (IDs 3, 4), lands on ID 5, leaves variable 2.
            Assert.Equal(VarType.Array | VarType.Variant, AssertInvokeOk(rs, "GetRows", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&getRowsTyped))));
            Assert.Equal(2, getRowsTyped);
            Assert.Equal(5, ReadIntField(rs, "ID"));

            // Typed ByRef Move(1) advances to ID 6; ByRef Variant Null fails 3421 as by value.
            AssertInvokeOk(rs, "Move", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&moveTyped)));
            Assert.Equal(1, moveTyped);
            Assert.Equal(6, ReadIntField(rs, "ID"));
            AssertInvokeDaoError(rs, "Move", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&nullVar)));

            // Call 26: rs.FindFirst(typed ByRef BSTR 'ID = 5') finds ID 5, leaves BSTR unchanged; ByRef Variant BSTR works; ByRef Variant Null -> 3421.
            AssertInvokeOk(rs, "FindFirst", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&findBstr)));
            Assert.Equal("ID = 5", Marshal.PtrToStringBSTR(findBstr));
            Assert.Equal(5, ReadIntField(rs, "ID"));
            AssertInvokeOk(rs, "FindFirst", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&findVar)));
            Assert.Equal(3, ReadIntField(rs, "ID"));
            AssertInvokeDaoError(rs, "FindFirst", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&nullVar)));

            // Call 46: rt.Seek(typed ByRef BSTR '=', ByRef Variant I4 3) lands on ID 3 and leaves both ByRef values unchanged.
            AssertInvokeOk(rt, "Seek", InvokeKind.Method,
                RawByRef(VarType.Bstr, (nint)(&eqBstr)),
                RawByRef(VarType.Variant, (nint)(&seekKeyVar)));
            Assert.Equal("=", Marshal.PtrToStringBSTR(eqBstr));
            Assert.Equal((ushort)VarType.I4, seekKeyVar.Vt);
            Assert.Equal(3, (int)seekKeyVar.Int64);
            Assert.Equal(3, ReadIntField(rt, "ID"));

            // Also works with ByRef Variant comparison + typed ByRef Key1; ByRef Variant Null comparison -> 3421; nested ByRef Key1 refused.
            AssertInvokeOk(rt, "Seek", InvokeKind.Method,
                RawByRef(VarType.Variant, (nint)(&geVar)),
                RawByRef(VarType.I4, (nint)(&seekKeyTyped)));
            Assert.Equal(4, seekKeyTyped);
            Assert.Equal(4, ReadIntField(rt, "ID"));
            AssertInvokeDaoError(rt, "Seek", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 1u,
                RawByRef(VarType.Variant, (nint)(&nullVar)), RawInt32(1));
            AssertInvokeNotModelled(rt, "Seek", InvokeKind.Method, "NotModelled.SeekKeyArgumentType",
                RawBstr("="), RawByRef(VarType.Variant, (nint)(&nestedKey)));
        }
        finally
        {
            Marshal.FreeBSTR(findBstr);
            Marshal.FreeBSTR(findVarBstr);
            Marshal.FreeBSTR(eqBstr);
            Marshal.FreeBSTR(geBstr);
        }

        Ok(Method(rs, "Close"));
        Ok(Method(rt, "Close"));
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
            // Workspace.CommitTrans (r-txn-1b/commit-option-vartypes): unrecorded VarTypes and overflow refused; Null -> 3421.
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
            AssertInvokeNotModelled(ws, "CommitTrans", InvokeKind.Method, "NotModelled.CommitTransArgumentConversion", RawDouble(4294967296.0));
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3421, "DAO.Workspace", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // Workspace.CreateDatabase: only Bstr recorded on p0/p1.
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawInt32(1), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawNull());

            // DBEngine.CommitTrans: unrecorded non-I4 VarTypes refused by name (recorded on Workspace only).
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments", RawSingle(0f));
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments", RawByRef(VarType.I4, (nint)(&byRefI4)));

            // DBEngine.CreateDatabase (r-wire/opt-createdatabase): unrecorded p0/p1 VarTypes and p0 Missing refused; p1 Null -> 3421, p1 Missing -> 3001.
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawInt32(1), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawByRef(VarType.Bstr, (nint)(&byRefBstr)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawInt32(1));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawDouble(1.0));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentConversion",
                RawMissing(), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"), RawMissing());
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3421, "DAO.DbEngine", publishesErrorInfo: true, expectedArgErr: 1u,
                RawBstr("dummy.accdb"), RawNull(), RawMissing());
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3001, "DAO.DbEngine", publishesErrorInfo: true, expectedArgErr: 1u,
                RawBstr("dummy.accdb"), RawMissing(), RawMissing());
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

        // Connection.Open (r-ado-1b/conn-open-args seq 111): Open(Missing, Null, Null) on ACE Provider -> Provider 0x80020005, argErr 0;
        // unrecorded VarTypes on p0/p1/p2/p3 are refused before coercion.
        Ok(Call(conn, "Provider", InvokeKind.PropertyPut, "Microsoft.ACE.OLEDB.12.0"));
        AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawInt32(123));
        AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawNull());
        AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawInt32(123));
        AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawMissing(), RawMissing(), RawBstr("16"));
        AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawMissing(), RawMissing(), RawDouble(4294967296.0));
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

    private static NativeVariant RawEmpty() => new() { Vt = (ushort)VarType.Empty };

    private static NativeVariant RawNull() => new() { Vt = (ushort)VarType.Null };

    private static NativeVariant RawByte(byte value) => new() { Vt = (ushort)VarType.UI1, Int64 = value };

    private static NativeVariant RawBool(bool value) => new() { Vt = (ushort)VarType.Bool, Int64 = value ? 0xFFFF : 0 };

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

    private int ReadIntField(DaoRecordsetObject rs, string name)
    {
        FieldsObject fields = Recover<FieldsObject>(Ok(Get(rs, "Fields")));
        FieldObject field = Recover<FieldObject>(Ok(Get(fields, "Item", name)));
        CallResult value = Ok(Get(field, "Value"));
        Assert.True(value.HasScalar && (VarType)value.Scalar.Vt == VarType.I4);
        return (int)value.Scalar.Int64;
    }

    [DllImport("oleaut32.dll")]
    private static extern int VariantClear(NativeVariant* pvarg);

    private static VarType AssertInvokeOk(
        DispatchObject target,
        string member,
        InvokeKind kind,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, _, _, NativeVariant result) = InvokeRawWithResult(target, member, kind, args);
        try
        {
            Assert.Equal(HResults.S_OK, hr);
            return (VarType)result.Vt;
        }
        finally
        {
            _ = VariantClear(&result);
            FreeExcep(excep);
        }
    }

    private static (int Hr, ExcepInfo Excep, uint ArgError, bool HasErrorInfo) InvokeRaw(
        DispatchObject target,
        string member,
        InvokeKind kind,
        NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, uint argError, bool hasErrorInfo, NativeVariant result) = InvokeRawWithResult(target, member, kind, args);
        _ = VariantClear(&result);
        return (hr, excep, argError, hasErrorInfo);
    }

    private static (int Hr, ExcepInfo Excep, uint ArgError, bool HasErrorInfo, NativeVariant Result) InvokeRawWithResult(
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
            return (hr, excep, argError, hasErrorInfo, result);
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
