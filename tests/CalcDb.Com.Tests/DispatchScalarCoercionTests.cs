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
    private const string RQ32Mdb = "cbce097e8ef4b8b84f6244a661c7e386a2469aab64057be068e5a169a2e3b1f2";
    private const string RQ2CursorAccdb = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2";
    private const string RAdo1bAccdb = "612dea90cd639ee52fd6abacf9033b19957eb30aa50f187b3774d50a74e33ea7";

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

        // Per-parameter VarType gating (q3-lv-getchunk-args-accdb seq 48-50, -mdb seq 32-34; ByRef: r-q35-byref-args/dao-select-getchunk-accdb):
        // UI1 is recorded on Bytes (p1) only; I2 and Bool are recorded on Offset (p0) only (by value or ByRef).
        byte byRefByte = 1;
        short byRefI2 = 4;
        short byRefBool = -1;
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawByte(1), RawInt32(4));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawByRef(VarType.UI1, (nint)(&byRefByte)), RawInt32(4));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawInt16(4));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawByRef(VarType.I2, (nint)(&byRefI2)));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawBool(true));
        AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), RawByRef(VarType.Bool, (nint)(&byRefBool)));

        // Unrecorded source VarTypes (by value, ByRef of unadmitted type, or nested ByRef) refused by name before VariantChangeTypeEx runs.
        int byRefI4Value = 4;
        float byRefR4 = 1.5f;
        NativeVariant innerDate = RawDate(45000.0);
        NativeVariant nestedByRefI4 = RawByRef(VarType.I4, (nint)(&byRefI4Value));
        NativeVariant[] unrecorded =
        [
            RawSingle(1.5f),
            RawCurrency(40000),
            RawDate(45000.0),
            RawDecimal(4m),
            RawInt64(4L),
            RawByRef(VarType.R4, (nint)(&byRefR4)),
            RawByRef(VarType.Variant, (nint)(&innerDate)),
            RawByRef(VarType.Variant, (nint)(&nestedByRefI4)),
        ];
        foreach (NativeVariant bad in unrecorded)
        {
            AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", bad, RawInt32(4));
            AssertInvokeNotModelled(memo, "GetChunk", InvokeKind.Method, "NotModelled.GetChunkArgumentType", RawInt32(0), bad);
        }

        // SelectRecordsetObject.Move also refuses unrecorded source VarTypes (including ByRef) before coercion.
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
        short byRefI2 = 50;
        float byRefR4 = 50f;
        double byRefDate = 1.0;
        double byRefDouble = 1.0;
        NativeVariant innerDate = RawDate(1.0);
        NativeVariant innerDouble = RawDouble(1.0);
        NativeVariant innerEmpty = RawEmpty();
        NativeVariant nestedByRefI4 = RawByRef(VarType.I4, (nint)(&byRefI4));
        NativeVariant nestedByRefR4 = RawByRef(VarType.R4, (nint)(&byRefR4));
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

            // PercentPosition put (r-cursor-1b/percent-assign; ByRef on ACCDB: r-q35-byref-args/dao-rs-percentposition):
            // unrecorded VarTypes (by value or ByRef), nested ByRef, and unrecorded overflow refused; Null -> 3421.
            foreach (NativeVariant bad in new[]
            {
                RawInt16(50),
                RawCurrency(500000),
                RawDate(50.0),
                RawInt64(50L),
                RawByRef(VarType.I2, (nint)(&byRefI2)),
                RawByRef(VarType.Date, (nint)(&byRefDate)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefR4)),
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

            // FindFirst/FindNext p0 (r-cursor-1b/find-expression-forms; ByRef: r-q2-cursor-dao/byref-dao-move-seek call 26, r-q35-byref-args/dao-rs-findlast):
            // unrecorded VarTypes (by value or ByRef) and nested ByRef refused on both.
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
            AssertInvokeDaoError(rs, "FindFirst", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // FindLast/FindPrevious p0 (r-wire/opt-find-criteria seq 47, 50, 53, 56; ByRef on ACCDB: r-q35-byref-args/dao-rs-findlast):
            // only Bstr and Null (by value or ByRef on ACCDB) are recorded; unadmitted/nested ByRef refused.
            foreach (NativeVariant bad in new[]
            {
                RawInt32(1),
                RawDouble(1.0),
                RawBool(true),
                RawEmpty(),
                RawByRef(VarType.I4, (nint)(&byRefI4)),
                RawByRef(VarType.Variant, (nint)(&innerEmpty)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)),
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

        int byRefI4 = 128;
        short byRefI2 = 0;
        float byRefR4 = 0f;
        NativeVariant innerI4 = RawInt32(128);
        NativeVariant innerNull = RawNull();
        NativeVariant nestedByRefI4 = RawByRef(VarType.I4, (nint)(&byRefI4));
        nint byRefBstr = Marshal.StringToBSTR("temp.accdb");
        NativeVariant nestedByRefBstr = RawByRef(VarType.Bstr, (nint)(&byRefBstr));
        try
        {
            // Workspace.CommitTrans (r-txn-1b/commit-option-vartypes; ByRef on ACCDB: r-q35-byref-args/dao-ws-committrans):
            // unrecorded VarTypes (by value or ByRef), nested ByRef, and overflow refused; Null -> 3421.
            foreach (NativeVariant bad in new[]
            {
                RawSingle(0f),
                RawCurrency(0),
                RawDate(0.0),
                RawDecimal(0m),
                RawInt64(0L),
                RawByRef(VarType.R4, (nint)(&byRefR4)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefI4)),
            })
            {
                AssertInvokeNotModelled(ws, "CommitTrans", InvokeKind.Method, "NotModelled.CommitTransArgumentType", bad);
            }
            AssertInvokeNotModelled(ws, "CommitTrans", InvokeKind.Method, "NotModelled.CommitTransArgumentConversion", RawDouble(4294967296.0));
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3421, "DAO.Workspace", publishesErrorInfo: true, expectedArgErr: 0u, RawNull());

            // Workspace.CreateDatabase: only Bstr (by value or ByRef) recorded on p0/p1; ByRef Option (p2) refused by name.
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawInt32(1), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawByRef(VarType.I4, (nint)(&byRefI4)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawNull());
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawByRef(VarType.Variant, (nint)(&innerNull)));
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(ws, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"), RawByRef(VarType.Variant, (nint)(&innerI4)));

            // DBEngine.CommitTrans: unrecorded non-I4 VarTypes (by value or ByRef) and nested ByRef refused by name.
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments", RawSingle(0f));
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments", RawByRef(VarType.I2, (nint)(&byRefI2)));
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments", RawByRef(VarType.Variant, (nint)(&nestedByRefI4)));

            // DBEngine.CreateDatabase (r-wire/opt-createdatabase; ByRef p0/p1: r-q35-byref-args/dao-eng-createdatabase):
            // unrecorded p0/p1 VarTypes, nested ByRef, ByRef p2 Option, and p0 Missing refused; p1 Null -> 3421, p1 Missing -> 3001.
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawInt32(1), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawByRef(VarType.I4, (nint)(&byRefI4)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawInt32(1));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawDouble(1.0));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(engineObj, "CreateDatabase", InvokeKind.Method, "NotModelled.CreateDatabaseArgumentType",
                RawBstr("dummy.accdb"), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0"), RawByRef(VarType.Variant, (nint)(&innerI4)));
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

        // Connection.Open (r-ado-1b/conn-open-args seq 111; ByRef on ACCDB: r-q35-byref-args/ado-conn-open):
        // Open(Missing, Null, Null) on ACE Provider -> Provider 0x80020005, argErr 0;
        // unrecorded VarTypes (by value or ByRef) and nested ByRef on p0/p1/p2/p3 are refused before coercion.
        Ok(Call(conn, "Provider", InvokeKind.PropertyPut, "Microsoft.ACE.OLEDB.12.0"));
        int byRefI4 = 1;
        short byRefI2 = 4;
        double byRefR8 = 1.0;
        NativeVariant innerI4 = RawInt32(1);
        NativeVariant nestedByRefI4 = RawByRef(VarType.I4, (nint)(&byRefI4));
        nint byRefBstr = Marshal.StringToBSTR("ID ASC");
        NativeVariant nestedByRefBstr = RawByRef(VarType.Bstr, (nint)(&byRefBstr));
        try
        {
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawInt32(123));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawNull());
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawInt32(123));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawMissing(), RawMissing(), RawBstr("16"));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.Bstr, (nint)(&byRefBstr)));
            AssertInvokeNotModelled(conn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType", RawMissing(), RawMissing(), RawMissing(), RawDouble(4294967296.0));
            AssertInvokeAdoError(conn, "Open", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
                RawMissing(), RawNull(), RawNull());

            string path = CopyFixture(RQ32Accdb, ".accdb");
            Ok(Method(conn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};"));
            Ok(Call(rs, "CursorLocation", InvokeKind.PropertyPut, 3)); // adUseClient
            Ok(Method(rs, "Open", "SELECT ID, FMemo FROM T ORDER BY ID", new DispatchPtr(connPtr), 3, 1));

            // Recordset.Sort put (r-ado-1b/view-sort-grammar; ByRef on ACCDB: r-q35-byref-args/ado-rs-sort):
            // unadmitted VarTypes (by value or ByRef) and nested ByRef refused; Null -> 0x80020005.
            foreach (NativeVariant bad in new[]
            {
                RawInt32(1),
                RawDouble(1.0),
                RawDate(1.0),
                RawByRef(VarType.I4, (nint)(&byRefI4)),
                RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)),
            })
            {
                AssertInvokeNotModelled(rs, "Sort", InvokeKind.PropertyPut, "NotModelled.AdoSortArgumentType", bad);
            }
            AssertInvokeAdoError(rs, "Sort", InvokeKind.PropertyPut, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u, RawNull());

            // Recordset.Find (r-ado-1b/view-find-client; ByRef p0/p1 on ACCDB: r-q35-byref-args/ado-rs-find):
            // unadmitted p0/p1 VarTypes, nested ByRef, and any ByRef on p2 (SearchDirection) or p3 (Start) refused by name.
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawInt32(1));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawByRef(VarType.Variant, (nint)(&nestedByRefBstr)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawDouble(1.0));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawBstr("1"));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawByRef(VarType.R8, (nint)(&byRefR8)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawByRef(VarType.Variant, (nint)(&nestedByRefI4)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawInt32(0), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawInt32(0), RawByRef(VarType.Variant, (nint)(&innerI4)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawInt32(0), RawInt32(1), RawByRef(VarType.I4, (nint)(&byRefI4)));
            AssertInvokeNotModelled(rs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType", RawBstr("ID = 1"), RawInt32(0), RawInt32(1), RawByRef(VarType.Variant, (nint)(&innerI4)));
            AssertInvokeAdoError(rs, "Find", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u, RawNull());

            // AdoFieldObject.GetChunk (r-oledb-odbc/stream-getchunk; ByRef on ACCDB: r-q35-byref-args/ado-field-getchunk):
            // unadmitted VarTypes (by value or ByRef) and nested ByRef refused by name.
            AdoFieldsObject fields = Recover<AdoFieldsObject>(Ok(Get(rs, "Fields")));
            AdoFieldObject field = Recover<AdoFieldObject>(Ok(Get(fields, "Item", "FMemo")));
            AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawInt16(4));
            AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawDouble(4.0));
            AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawByRef(VarType.I2, (nint)(&byRefI2)));
            AssertInvokeNotModelled(field, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg", RawByRef(VarType.Variant, (nint)(&nestedByRefI4)));
        }
        finally
        {
            Marshal.FreeBSTR(byRefBstr);
        }

        Ok(Method(rs, "Close"));
        Ok(Method(conn, "Close"));
    }

    [Theory]
    [InlineData(RQ32Accdb, ".accdb")] // r-q35-byref-args/dao-select-getchunk-accdb a1 calls 9-26, 35-53
    [InlineData(RQ32Mdb, ".mdb")]     // r-q35-byref-args/dao-select-getchunk-mdb a1 calls 9-26, 35-53
    public void Dao_SelectField_GetChunk_ByRef_matches_by_value_on_ACCDB_and_MDB(string fixtureHash, string ext)
    {
        DatabaseObject db = OpenFixture(fixtureHash, ext);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset",
            "SELECT ID, FMemo, FOle FROM T WHERE ID = 3", 2)));
        SelectFieldsObject fields = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        SelectFieldObject memo = Recover<SelectFieldObject>(Ok(Get(fields, "Item", "FMemo")));
        SelectFieldObject ole = Recover<SelectFieldObject>(Ok(Get(fields, "Item", "FOle")));

        int p0I4 = 0, p1I4 = 4;
        short p0I2 = 2, p0False = 0, p0True = -1;
        byte p1UI1 = 3;
        double p0R8 = 1.5, p1R8 = 2.5, p1Overflow = 4294967296.0;
        nint bstrTwo = Marshal.StringToBSTR("2");
        nint bstrThree = Marshal.StringToBSTR("3");
        nint bstrBad = Marshal.StringToBSTR("abc");
        NativeVariant varI4Zero = RawInt32(0), varI2Two = RawInt16(2), varTrue = RawBool(true);
        NativeVariant varR8OneHalf = RawDouble(1.5), varEmpty = RawEmpty(), varNull = RawNull();
        NativeVariant varI4Four = RawInt32(4), varUI1Three = RawByte(3), varOverflow = RawDouble(4294967296.0);
        NativeVariant varBstrTwo = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrTwo };
        NativeVariant varBstrBad = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrBad };
        try
        {
            // p0 Offset (a1 calls 9, 10, 11, 12, 13, 15, 16, 19, 21, 22, 23, 24, 25, 26)
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&p0I4)), RawInt32(4)));
            Assert.Equal(0, p0I4);
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varI4Zero)), RawInt32(4)));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.I2, (nint)(&p0I2)), RawInt32(4)));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varI2Two)), RawInt32(4)));
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Bool, (nint)(&p0False)), RawInt32(4)));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: false, expectedArgErr: 0u,
                RawByRef(VarType.Bool, (nint)(&p0True)), RawInt32(4));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: false, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varTrue)), RawInt32(4));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.R8, (nint)(&p0R8)), RawInt32(4)));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varR8OneHalf)), RawInt32(4)));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&bstrTwo)), RawInt32(4)));
            Assert.Equal("cdef", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varBstrTwo)), RawInt32(4)));
            Assert.Equal("2", Marshal.PtrToStringBSTR(bstrTwo));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
                RawByRef(VarType.Bstr, (nint)(&bstrBad)), RawInt32(4));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
                RawByRef(VarType.Variant, (nint)(&varBstrBad)), RawInt32(4));
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varEmpty)), RawInt32(4)));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
                RawByRef(VarType.Variant, (nint)(&varNull)), RawInt32(4));

            // p1 Bytes (a1 calls 35, 36, 37, 38, 39, 41, 42, 43, 45, 47, 48)
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.I4, (nint)(&p1I4))));
            Assert.Equal("abcd", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.Variant, (nint)(&varI4Four))));
            Assert.Equal("abc", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.UI1, (nint)(&p1UI1))));
            Assert.Equal("abc", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.Variant, (nint)(&varUI1Three))));
            Assert.Equal("ab", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.R8, (nint)(&p1R8))));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
                RawInt32(0), RawByRef(VarType.R8, (nint)(&p1Overflow)));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3001, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
                RawInt32(0), RawByRef(VarType.Variant, (nint)(&varOverflow)));
            Assert.Equal("abc", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.Bstr, (nint)(&bstrThree))));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
                RawInt32(0), RawByRef(VarType.Bstr, (nint)(&bstrBad)));
            Assert.Equal("", InvokeOkText(memo, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.Variant, (nint)(&varEmpty))));
            AssertInvokeDaoError(memo, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 0u,
                RawInt32(0), RawByRef(VarType.Variant, (nint)(&varNull)));

            // FOle spot checks (a1 calls 49-53)
            Assert.Equal(VarType.Array | VarType.UI1, AssertInvokeOk(ole, "GetChunk", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&p0I4)), RawInt32(4)));
            Assert.Equal(VarType.Array | VarType.UI1, AssertInvokeOk(ole, "GetChunk", InvokeKind.Method, RawInt32(0), RawByRef(VarType.Variant, (nint)(&varI4Four))));
            AssertInvokeDaoError(ole, "GetChunk", InvokeKind.Method, 3421, "DAO.Field", publishesErrorInfo: true, expectedArgErr: 1u,
                RawByRef(VarType.Variant, (nint)(&varNull)), RawInt32(4));
        }
        finally
        {
            Marshal.FreeBSTR(bstrTwo);
            Marshal.FreeBSTR(bstrThree);
            Marshal.FreeBSTR(bstrBad);
        }

        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dao_PercentPosition_Find_CommitTrans_and_CreateDatabase_ByRef_match_by_value_on_ACCDB_and_refuse_MDB()
    {
        // ACCDB Recordset: PercentPosition put (r-q35-byref-args/dao-rs-percentposition a1 calls 10, 15, 24, 29, 34, 39, 44, 49, 53, 57)
        // and FindLast/FindNext/FindPrevious (r-q35-byref-args/dao-rs-findlast a1 calls 8, 12, 15, 19, 23, 27, 31).
        DatabaseObject accdb = OpenFixture(RQ2CursorAccdb, ".accdb");
        DaoRecordsetObject rs = Recover<DaoRecordsetObject>(Ok(Method(accdb, "OpenRecordset", "T", 2)));
        Ok(Method(rs, "MoveLast"));
        Ok(Method(rs, "MoveFirst"));

        int ppI4 = 50, commitI4 = 0;
        short commitI2 = 0, commitTrue = -1;
        byte commitUI1 = 0;
        float ppR4 = 25f;
        double ppR8 = 75.0, commitR8 = 0.0;
        nint ppBstr = Marshal.StringToBSTR("50");
        nint commitBstr = Marshal.StringToBSTR("0");
        nint critLast = Marshal.StringToBSTR("ID >= 2");
        nint critNext = Marshal.StringToBSTR("ID >= 4");
        nint critPrev = Marshal.StringToBSTR("ID <= 3");
        nint localeValid = Marshal.StringToBSTR(";LANGID=0x0409;CP=1252;COUNTRY=0");
        nint localeEmpty = Marshal.StringToBSTR("");
        string createPath1 = Path.Combine(Path.GetTempPath(), $"calcdb-byref-c1-{Guid.NewGuid():N}.accdb");
        string createPath2 = Path.Combine(Path.GetTempPath(), $"calcdb-byref-c2-{Guid.NewGuid():N}.accdb");
        string createPath3 = Path.Combine(Path.GetTempPath(), $"calcdb-byref-c3-{Guid.NewGuid():N}.accdb");
        string createPath4 = Path.Combine(Path.GetTempPath(), $"calcdb-byref-c4-{Guid.NewGuid():N}.accdb");
        _files.AddRange([createPath1, createPath2, createPath3, createPath4]);
        nint bstrPath1 = Marshal.StringToBSTR(createPath1);
        nint bstrPath2 = Marshal.StringToBSTR(createPath2);
        nint bstrPath3 = Marshal.StringToBSTR(createPath3);
        nint bstrPath4 = Marshal.StringToBSTR(createPath4);

        NativeVariant varI4Fifty = RawInt32(50), varR4 = RawSingle(25f), varR8 = RawDouble(75.0);
        NativeVariant varEmpty = RawEmpty(), varNull = RawNull(), varCommitI4 = RawInt32(0), varCommitTrue = RawBool(true);
        NativeVariant varPpBstr = new() { Vt = (ushort)VarType.Bstr, Pointer = ppBstr };
        NativeVariant varCritLast = new() { Vt = (ushort)VarType.Bstr, Pointer = critLast };
        NativeVariant varCritNext = new() { Vt = (ushort)VarType.Bstr, Pointer = critNext };
        NativeVariant varCritPrev = new() { Vt = (ushort)VarType.Bstr, Pointer = critPrev };
        NativeVariant varPath2 = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrPath2 };
        NativeVariant varLocaleValid = new() { Vt = (ushort)VarType.Bstr, Pointer = localeValid };
        NativeVariant varLocaleEmpty = new() { Vt = (ushort)VarType.Bstr, Pointer = localeEmpty };
        try
        {
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.I4, (nint)(&ppI4)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varI4Fifty)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.R4, (nint)(&ppR4)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varR4)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.R8, (nint)(&ppR8)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varR8)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Bstr, (nint)(&ppBstr)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varPpBstr)));
            AssertInvokeOk(rs, "PercentPosition", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varEmpty)));
            AssertInvokeDaoError(rs, "PercentPosition", InvokeKind.PropertyPut, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varNull)));
            Assert.Equal(50, ppI4);

            AssertInvokeOk(rs, "FindLast", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&critLast)));
            AssertInvokeOk(rs, "FindLast", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varCritLast)));
            AssertInvokeDaoError(rs, "FindLast", InvokeKind.Method, 3421, "DAO.Recordset", publishesErrorInfo: true, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varNull)));
            Ok(Method(rs, "MoveFirst"));
            AssertInvokeOk(rs, "FindNext", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&critNext)));
            AssertInvokeOk(rs, "FindNext", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varCritNext)));
            AssertInvokeOk(rs, "FindPrevious", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&critPrev)));
            AssertInvokeOk(rs, "FindPrevious", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varCritPrev)));
            Assert.Equal("ID >= 2", Marshal.PtrToStringBSTR(critLast));
            Ok(Method(rs, "Close"));

            // Workspace and DBEngine CommitTrans (r-q35-byref-args/dao-ws-committrans a1 calls 10-62, 74, 78).
            WorkspaceObject ws = _engine.DefaultWorkspace;
            nint enginePtr = 0;
            Assert.Equal(HResults.S_OK, DBEngineObject.Create(_binding, 0, ComIids.IDispatch, &enginePtr));
            _held.Add(enginePtr);
            DBEngineObject engineObj = ComObject.Recover<DBEngineObject>(enginePtr);

            foreach (NativeVariant okOpt in new[]
            {
                RawByRef(VarType.I4, (nint)(&commitI4)),
                RawByRef(VarType.Variant, (nint)(&varCommitI4)),
                RawByRef(VarType.I2, (nint)(&commitI2)),
                RawByRef(VarType.UI1, (nint)(&commitUI1)),
                RawByRef(VarType.R8, (nint)(&commitR8)),
                RawByRef(VarType.Bstr, (nint)(&commitBstr)),
                RawByRef(VarType.Variant, (nint)(&varEmpty)),
            })
            {
                Ok(Method(ws, "BeginTrans"));
                AssertInvokeOk(ws, "CommitTrans", InvokeKind.Method, okOpt);
            }
            Ok(Method(ws, "BeginTrans"));
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3001, "DAO.Workspace", publishesErrorInfo: false, expectedArgErr: 0u,
                RawByRef(VarType.Bool, (nint)(&commitTrue)));
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3001, "DAO.Workspace", publishesErrorInfo: false, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varCommitTrue)));
            AssertInvokeDaoError(ws, "CommitTrans", InvokeKind.Method, 3421, "DAO.Workspace", publishesErrorInfo: true, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varNull)));
            Ok(Method(ws, "Rollback"));

            Ok(Method(engineObj, "BeginTrans"));
            AssertInvokeOk(engineObj, "CommitTrans", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&commitI4)));
            Ok(Method(engineObj, "BeginTrans"));
            AssertInvokeOk(engineObj, "CommitTrans", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varCommitI4)));

            // DBEngine & Workspace CreateDatabase p0/p1 (r-q35-byref-args/dao-eng-createdatabase a1 calls 7, 11, 15, 19, 23, 27, 31, 35, 39, 47).
            Assert.Equal(VarType.Dispatch, InvokeOkAndCloseDatabase(engineObj, "CreateDatabase",
                RawByRef(VarType.Bstr, (nint)(&bstrPath1)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0")));
            Assert.Equal(VarType.Dispatch, InvokeOkAndCloseDatabase(engineObj, "CreateDatabase",
                RawByRef(VarType.Variant, (nint)(&varPath2)), RawByRef(VarType.Variant, (nint)(&varLocaleValid)), RawInt32(128)));
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3001, "DAO.Workspace", publishesErrorInfo: false, expectedArgErr: 0u,
                RawBstr(createPath3), RawByRef(VarType.Bstr, (nint)(&localeEmpty)), RawInt32(128));
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3001, "DAO.Workspace", publishesErrorInfo: false, expectedArgErr: 0u,
                RawBstr(createPath3), RawByRef(VarType.Variant, (nint)(&varLocaleEmpty)), RawInt32(128));
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3001, "DAO.Workspace", publishesErrorInfo: false, expectedArgErr: 0u,
                RawBstr(createPath3), RawByRef(VarType.Variant, (nint)(&varEmpty)), RawInt32(128));
            AssertInvokeDaoError(engineObj, "CreateDatabase", InvokeKind.Method, 3421, "DAO.DbEngine", publishesErrorInfo: true, expectedArgErr: 1u,
                RawBstr(createPath3), RawByRef(VarType.Variant, (nint)(&varNull)), RawInt32(128));
            Assert.Equal(VarType.Dispatch, InvokeOkAndCloseDatabase(ws, "CreateDatabase",
                RawByRef(VarType.Bstr, (nint)(&bstrPath3)), RawBstr(";LANGID=0x0409;CP=1252;COUNTRY=0")));
            Assert.Equal(VarType.Dispatch, InvokeOkAndCloseDatabase(ws, "CreateDatabase",
                RawBstr(createPath4), RawByRef(VarType.Bstr, (nint)(&localeValid))));

            // MDB refusal boundary: members recorded on ACCDB only MUST keep ByRef refused by name on MDB.
            string mdbPath = CopyFixture(RQ32Mdb, ".mdb");
            DatabaseObject mdb = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Arg(mdbPath), Arg(false), Arg(true)])));
            DatabaseObject engMdb = Recover<DatabaseObject>(Ok(Method(engineObj, "OpenDatabase", mdbPath, false, true)));
            DaoRecordsetObject mdbRs = Recover<DaoRecordsetObject>(Ok(Method(mdb, "OpenRecordset", "T", 2)));
            AssertInvokeNotModelled(mdbRs, "PercentPosition", InvokeKind.PropertyPut, "NotModelled.PercentPositionArgumentType",
                RawByRef(VarType.I4, (nint)(&ppI4)));
            AssertInvokeNotModelled(mdbRs, "FindFirst", InvokeKind.Method, "NotModelled.FindCriteriaType",
                RawByRef(VarType.Bstr, (nint)(&critLast)));
            AssertInvokeNotModelled(mdbRs, "FindLast", InvokeKind.Method, "NotModelled.FindCriteriaType",
                RawByRef(VarType.Bstr, (nint)(&critLast)));
            AssertInvokeNotModelled(mdbRs, "FindNext", InvokeKind.Method, "NotModelled.FindCriteriaType",
                RawByRef(VarType.Bstr, (nint)(&critNext)));
            AssertInvokeNotModelled(mdbRs, "FindPrevious", InvokeKind.Method, "NotModelled.FindCriteriaType",
                RawByRef(VarType.Bstr, (nint)(&critPrev)));
            AssertInvokeNotModelled(mdbRs, "Move", InvokeKind.Method, "NotModelled.MoveArgumentType",
                RawByRef(VarType.I4, (nint)(&ppI4)));
            AssertInvokeNotModelled(ws, "CommitTrans", InvokeKind.Method, "NotModelled.CommitTransArgumentType",
                RawByRef(VarType.I4, (nint)(&commitI4)));
            AssertInvokeNotModelled(engineObj, "CommitTrans", InvokeKind.Method, "NotModelled.EngineTransactionArguments",
                RawByRef(VarType.I4, (nint)(&commitI4)));
            Ok(Method(mdbRs, "Close"));
            Ok(Method(mdb, "Close"));
            Ok(Method(engMdb, "Close"));
        }
        finally
        {
            Marshal.FreeBSTR(ppBstr);
            Marshal.FreeBSTR(commitBstr);
            Marshal.FreeBSTR(critLast);
            Marshal.FreeBSTR(critNext);
            Marshal.FreeBSTR(critPrev);
            Marshal.FreeBSTR(localeValid);
            Marshal.FreeBSTR(localeEmpty);
            Marshal.FreeBSTR(bstrPath1);
            Marshal.FreeBSTR(bstrPath2);
            Marshal.FreeBSTR(bstrPath3);
            Marshal.FreeBSTR(bstrPath4);
        }
    }

    [Fact]
    public void Ado_Open_Sort_Find_and_GetChunk_ByRef_match_by_value_on_ACCDB_and_refuse_MDB()
    {
        string accdbPath = CopyFixture(RAdo1bAccdb, ".accdb");
        string lvAccdbPath = CopyFixture(RQ32Accdb, ".accdb");
        string mdbPath = CopyFixture(RQ32Mdb, ".mdb");

        nint connPtr = 0, rsPtr = 0, lvConnPtr = 0, lvRsPtr = 0, mdbConnPtr = 0, mdbRsPtr = 0;
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(_binding, 0, ComIids.IDispatch, &connPtr));
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_binding, 0, ComIids.IDispatch, &rsPtr));
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(_binding, 0, ComIids.IDispatch, &lvConnPtr));
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_binding, 0, ComIids.IDispatch, &lvRsPtr));
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(_binding, 0, ComIids.IDispatch, &mdbConnPtr));
        Assert.Equal(HResults.S_OK, AdoRecordsetObject.Create(_binding, 0, ComIids.IDispatch, &mdbRsPtr));
        _held.AddRange([connPtr, rsPtr, lvConnPtr, lvRsPtr, mdbConnPtr, mdbRsPtr]);
        ConnectionObject conn = ComObject.Recover<ConnectionObject>(connPtr);
        AdoRecordsetObject rs = ComObject.Recover<AdoRecordsetObject>(rsPtr);
        ConnectionObject lvConn = ComObject.Recover<ConnectionObject>(lvConnPtr);
        AdoRecordsetObject lvRs = ComObject.Recover<AdoRecordsetObject>(lvRsPtr);
        ConnectionObject mdbConn = ComObject.Recover<ConnectionObject>(mdbConnPtr);
        AdoRecordsetObject mdbRs = ComObject.Recover<AdoRecordsetObject>(mdbRsPtr);

        string csAccdb = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={accdbPath};Mode=Read";
        string csNoDataSource = "Provider=Microsoft.ACE.OLEDB.12.0;Mode=Read";
        string csMdb = $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={mdbPath};Mode=Read";

        nint bstrCsAccdb = Marshal.StringToBSTR(csAccdb);
        nint bstrCsNoDataSource = Marshal.StringToBSTR(csNoDataSource);
        nint bstrCsMdb = Marshal.StringToBSTR(csMdb);
        nint bstrAdmin = Marshal.StringToBSTR("Admin");
        nint bstrEmpty = Marshal.StringToBSTR("");
        nint bstrSortDesc = Marshal.StringToBSTR("ID DESC");
        nint bstrFindL20 = Marshal.StringToBSTR("L = 20");
        int optNegOne = -1, opt99 = 99, skip0 = 0, skip1 = 1, skipNeg1 = -1, chunk4 = 4, chunk0 = 0, chunkNeg1 = -1;
        NativeVariant varCsAccdb = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrCsAccdb };
        NativeVariant varAdmin = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrAdmin };
        NativeVariant varEmptyBstr = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrEmpty };
        NativeVariant varSortDesc = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrSortDesc };
        NativeVariant varFindL20 = new() { Vt = (ushort)VarType.Bstr, Pointer = bstrFindL20 };
        NativeVariant varNull = RawNull(), varOptNegOne = RawInt32(-1), varOpt99 = RawInt32(99);
        NativeVariant varSkip1 = RawInt32(1), varChunk4 = RawInt32(4), varChunkNeg1 = RawInt32(-1);
        try
        {
            ConnectionObject NewAdoConn(bool configure)
            {
                nint ptr = 0;
                Assert.Equal(HResults.S_OK, ConnectionObject.Create(_binding, 0, ComIids.IDispatch, &ptr));
                _held.Add(ptr);
                ConnectionObject c = ComObject.Recover<ConnectionObject>(ptr);
                if (configure)
                {
                    Ok(Call(c, "ConnectionString", InvokeKind.PropertyPut, csAccdb));
                }
                return c;
            }

            // Connection.Open p0-p3 on ACCDB (r-q35-byref-args/ado-conn-open a1 calls 7, 19, 29, 64, 77, 90, 110, 136, 156, 180, 194).
            ConnectionObject c0a = NewAdoConn(configure: false);
            AssertInvokeOk(c0a, "Open", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&bstrCsAccdb)));
            Ok(Method(c0a, "Close"));
            ConnectionObject c0b = NewAdoConn(configure: false);
            AssertInvokeOk(c0b, "Open", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varCsAccdb)));
            Ok(Method(c0b, "Close"));
            ConnectionObject noDsConn = NewAdoConn(configure: false);
            AssertInvokeAdoError(noDsConn, "Open", InvokeKind.Method, unchecked((int)0x80040E4D), expectedArgErr: 0u,
                RawByRef(VarType.Bstr, (nint)(&bstrCsNoDataSource)));
            Assert.Equal(0, Int32Value(Ok(Get(noDsConn, "State"))));
            Assert.Equal(1, Int32Value(Ok(Get(Recover<AdoErrorsObject>(Ok(Get(noDsConn, "Errors"))), "Count"))));

            ConnectionObject c1a = NewAdoConn(configure: true);
            AssertInvokeOk(c1a, "Open", InvokeKind.Method, RawMissing(), RawByRef(VarType.Bstr, (nint)(&bstrAdmin)), RawByRef(VarType.Bstr, (nint)(&bstrEmpty)));
            Ok(Method(c1a, "Close"));
            ConnectionObject c1b = NewAdoConn(configure: true);
            AssertInvokeOk(c1b, "Open", InvokeKind.Method, RawMissing(), RawByRef(VarType.Variant, (nint)(&varAdmin)), RawByRef(VarType.Variant, (nint)(&varEmptyBstr)));
            Ok(Method(c1b, "Close"));
            AssertInvokeAdoError(NewAdoConn(configure: true), "Open", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
                RawMissing(), RawByRef(VarType.Variant, (nint)(&varNull)), RawMissing());
            AssertInvokeAdoError(NewAdoConn(configure: true), "Open", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
                RawMissing(), RawBstr("Admin"), RawByRef(VarType.Variant, (nint)(&varNull)));
            ConnectionObject c3c = NewAdoConn(configure: true);
            AssertInvokeOk(c3c, "Open", InvokeKind.Method, RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.I4, (nint)(&opt99)));
            Assert.Equal(0, Int32Value(Ok(Get(c3c, "State"))));
            Assert.Equal(0, Int32Value(Ok(Get(Recover<AdoErrorsObject>(Ok(Get(c3c, "Errors"))), "Count"))));
            ConnectionObject c3d = NewAdoConn(configure: true);
            AssertInvokeOk(c3d, "Open", InvokeKind.Method, RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.Variant, (nint)(&varOpt99)));
            Assert.Equal(0, Int32Value(Ok(Get(c3d, "State"))));
            Assert.Equal(0, Int32Value(Ok(Get(Recover<AdoErrorsObject>(Ok(Get(c3d, "Errors"))), "Count"))));
            ConnectionObject c3a = NewAdoConn(configure: true);
            AssertInvokeOk(c3a, "Open", InvokeKind.Method, RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.I4, (nint)(&optNegOne)));
            Ok(Method(c3a, "Close"));
            Ok(Call(conn, "ConnectionString", InvokeKind.PropertyPut, csAccdb));
            AssertInvokeOk(conn, "Open", InvokeKind.Method, RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.Variant, (nint)(&varOptNegOne)));

            // Recordset.Sort put (r-q35-byref-args/ado-rs-sort a1 calls 19, 24, 29, 34, 39)
            // and Recordset.Find p0/p1 (r-q35-byref-args/ado-rs-find a1 calls 19, 24, 29, 35, 41, 52, 55, 59, 62, 66, 70).
            Ok(Call(rs, "CursorLocation", InvokeKind.PropertyPut, 3));
            Ok(Method(rs, "Open", "SELECT ID, L, S FROM T ORDER BY ID", new DispatchPtr(connPtr), 3, 1, 1));
            AssertInvokeOk(rs, "Sort", InvokeKind.PropertyPut, RawByRef(VarType.Bstr, (nint)(&bstrSortDesc)));
            AssertInvokeOk(rs, "Sort", InvokeKind.PropertyPut, RawByRef(VarType.Variant, (nint)(&varSortDesc)));
            AssertInvokeOk(rs, "Sort", InvokeKind.PropertyPut, RawByRef(VarType.Bstr, (nint)(&bstrEmpty)));
            AssertInvokeAdoError(rs, "Sort", InvokeKind.PropertyPut, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varNull)));

            Ok(Method(rs, "MoveFirst"));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawByRef(VarType.Bstr, (nint)(&bstrFindL20)));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varFindL20)));
            AssertInvokeAdoError(rs, "Find", InvokeKind.Method, unchecked((int)0x800A0BB9), expectedArgErr: 0u,
                RawByRef(VarType.Bstr, (nint)(&bstrEmpty)));
            AssertInvokeAdoError(rs, "Find", InvokeKind.Method, HResults.DISP_E_TYPEMISMATCH, expectedArgErr: 0u,
                RawByRef(VarType.Variant, (nint)(&varNull)));
            Ok(Method(rs, "MoveFirst"));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawBstr("L = 20"), RawByRef(VarType.I4, (nint)(&skip0)));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawBstr("L = 20"), RawByRef(VarType.I4, (nint)(&skip1)));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawBstr("L = 20"), RawByRef(VarType.Variant, (nint)(&varSkip1)));
            Ok(Method(rs, "MoveFirst"));
            AssertInvokeOk(rs, "Find", InvokeKind.Method, RawBstr("L = 20"), RawByRef(VarType.I4, (nint)(&skipNeg1)));
            Ok(Method(rs, "Close"));
            Ok(Method(conn, "Close"));

            // ADO Field.GetChunk p0 on server forward-only (2,0,1) and client static (3,3,1) ACCDB
            // (r-q35-byref-args/ado-field-getchunk a1 calls 18-25, 37-44).
            Ok(Method(lvConn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={lvAccdbPath};Mode=Read"));
            foreach ((int loc, int cur) in new[] { (2, 0), (3, 3) })
            {
                Ok(Call(lvRs, "CursorLocation", InvokeKind.PropertyPut, loc));
                Ok(Method(lvRs, "Open", "SELECT ID, FMemo, FOle FROM T WHERE ID = 3", new DispatchPtr(lvConnPtr), cur, 1, 1));
                AdoFieldsObject lvFields = Recover<AdoFieldsObject>(Ok(Get(lvRs, "Fields")));
                AdoFieldObject lvOle = Recover<AdoFieldObject>(Ok(Get(lvFields, "Item", "FOle")));
                AdoFieldObject lvMemo = Recover<AdoFieldObject>(Ok(Get(lvFields, "Item", "FMemo")));
                Assert.Equal(VarType.Array | VarType.UI1, AssertInvokeOk(lvOle, "GetChunk", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&chunk4))));
                Assert.Equal(VarType.Array | VarType.UI1, AssertInvokeOk(lvOle, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varChunk4))));
                AssertInvokeAdoError(lvOle, "GetChunk", InvokeKind.Method, unchecked((int)0x800A0BB9), expectedArgErr: 0u,
                    RawByRef(VarType.I4, (nint)(&chunk0)));
                AssertInvokeAdoError(lvOle, "GetChunk", InvokeKind.Method, unchecked((int)0x800A0BB9), expectedArgErr: 0u,
                    RawByRef(VarType.I4, (nint)(&chunkNeg1)));
                AssertInvokeAdoError(lvOle, "GetChunk", InvokeKind.Method, unchecked((int)0x800A0BB9), expectedArgErr: 0u,
                    RawByRef(VarType.Variant, (nint)(&varChunkNeg1)));
                Assert.Equal("abcd", InvokeOkText(lvMemo, "GetChunk", InvokeKind.Method, RawByRef(VarType.I4, (nint)(&chunk4))));
                Assert.Equal("efgh", InvokeOkText(lvMemo, "GetChunk", InvokeKind.Method, RawByRef(VarType.Variant, (nint)(&varChunk4))));
                Assert.Equal(4, chunk4);
                Ok(Method(lvRs, "Close"));
            }
            Ok(Method(lvConn, "Close"));

            // MDB refusal boundary: Connection.Open, Recordset.Sort, Recordset.Find, and Field.GetChunk MUST refuse ByRef on MDB.
            AssertInvokeNotModelled(mdbConn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType",
                RawByRef(VarType.Bstr, (nint)(&bstrCsMdb)));
            Ok(Call(mdbConn, "ConnectionString", InvokeKind.PropertyPut, csMdb));
            AssertInvokeNotModelled(mdbConn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType",
                RawMissing(), RawByRef(VarType.Bstr, (nint)(&bstrAdmin)), RawMissing());
            AssertInvokeNotModelled(mdbConn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType",
                RawMissing(), RawByRef(VarType.Variant, (nint)(&varNull)), RawMissing());
            AssertInvokeNotModelled(mdbConn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType",
                RawMissing(), RawMissing(), RawByRef(VarType.Bstr, (nint)(&bstrEmpty)));
            AssertInvokeNotModelled(mdbConn, "Open", InvokeKind.Method, "NotModelled.AdoConnectionOpenArgumentType",
                RawMissing(), RawMissing(), RawMissing(), RawByRef(VarType.I4, (nint)(&optNegOne)));
            Ok(Method(mdbConn, "Open"));
            Ok(Call(mdbRs, "CursorLocation", InvokeKind.PropertyPut, 3));
            Ok(Method(mdbRs, "Open", "SELECT ID, FMemo FROM T ORDER BY ID", new DispatchPtr(mdbConnPtr), 3, 1, 1));
            AssertInvokeNotModelled(mdbRs, "Sort", InvokeKind.PropertyPut, "NotModelled.AdoSortArgumentType",
                RawByRef(VarType.Bstr, (nint)(&bstrSortDesc)));
            AssertInvokeNotModelled(mdbRs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType",
                RawByRef(VarType.Bstr, (nint)(&bstrFindL20)));
            AssertInvokeNotModelled(mdbRs, "Find", InvokeKind.Method, "NotModelled.AdoFindArgumentType",
                RawBstr("ID = 1"), RawByRef(VarType.I4, (nint)(&skip0)));
            AdoFieldsObject mdbFields = Recover<AdoFieldsObject>(Ok(Get(mdbRs, "Fields")));
            AdoFieldObject mdbMemo = Recover<AdoFieldObject>(Ok(Get(mdbFields, "Item", "FMemo")));
            AssertInvokeNotModelled(mdbMemo, "GetChunk", InvokeKind.Method, "NotModelled.AdoGetChunkArg",
                RawByRef(VarType.I4, (nint)(&chunk4)));
            Ok(Method(mdbRs, "Close"));
            Ok(Method(mdbConn, "Close"));
        }
        finally
        {
            Marshal.FreeBSTR(bstrCsAccdb);
            Marshal.FreeBSTR(bstrCsNoDataSource);
            Marshal.FreeBSTR(bstrCsMdb);
            Marshal.FreeBSTR(bstrAdmin);
            Marshal.FreeBSTR(bstrEmpty);
            Marshal.FreeBSTR(bstrSortDesc);
            Marshal.FreeBSTR(bstrFindL20);
        }
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

    private static string InvokeOkText(
        DispatchObject target,
        string member,
        InvokeKind kind,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, _, _, NativeVariant result) = InvokeRawWithResult(target, member, kind, args);
        try
        {
            Assert.Equal(HResults.S_OK, hr);
            Assert.Equal(VarType.Bstr, (VarType)result.Vt);
            return result.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(result.Pointer);
        }
        finally
        {
            _ = VariantClear(&result);
            FreeExcep(excep);
        }
    }

    private VarType InvokeOkAndCloseDatabase(
        DispatchObject target,
        string member,
        params NativeVariant[] args)
    {
        (int hr, ExcepInfo excep, _, _, NativeVariant result) = InvokeRawWithResult(target, member, InvokeKind.Method, args);
        try
        {
            Assert.Equal(HResults.S_OK, hr);
            Assert.Equal(VarType.Dispatch, (VarType)result.Vt);
            DatabaseObject created = ComObject.Recover<DatabaseObject>(result.Pointer);
            Ok(Method(created, "Close"));
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

    private static int Int32Value(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.I4, result.Failure?.Description);
        return unchecked((int)result.Scalar.Int64);
    }
}
