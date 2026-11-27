using System.Runtime.InteropServices;

using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Evidence;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// Wave 19 COM/DAO/ADO core stops (W-08, W-09, R-DAO-1, R-CURSOR-1, R-ADO-1, R-ERR-1, R-LOCK-3):
/// <list type="bullet">
/// <item><c>dao-named-3001</c>: repeated named arguments and positional+named collisions on <c>Database.OpenRecordset</c>
/// fail DAO 3001 with IErrorInfo and DBEngine.Errors, and unknown named parameter on <c>Recordset.MoveNext</c> records
/// 3265 from <c>DAO.Fields</c> (<c>evidence/r-q2-cursor-dao/named-dao-refusals</c> #37/#50/#52,
/// <c>evidence/r-q2-cursor-dao/named-dao-order-variants</c> #45).</item>
/// <item><c>snap-noorder-chunk</c>: unordered ACCDB single-table <c>SELECT</c> snapshot lazy 500-row chunk
/// materialization across intervening <c>UPDATE</c>s (<c>evidence/r-q2-cursor-dao/pop-snapshot-select-noorder-big</c>).</item>
/// <item><c>w17-sql-like-range</c> DAO follow-up: <c>DatabaseSql.OpenDerived</c> passes <c>derived: true</c> for
/// derived <c>Sort</c> tie ordering (<c>evidence/r-cursor-1b/sort-expression-forms</c>).</item>
/// <item><c>w17-jk44-vis</c> ADO follow-up: ADO <c>SELECT</c> on a table dropped by another in-process session fails
/// <c>0x80040E37</c> / <c>3011</c> / <c>-543884569</c> while <c>Connection.Catalog</c> stays stale
/// (<c>evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb</c>, <c>vis-cross-ddl-mdb</c> calls 124-147).</item>
/// </list>
/// </summary>
public sealed unsafe class Wave19ComDaoCoreTests : IDisposable
{
    private const string Q2CursorDaoMain = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2"; // r-q2-cursor-dao-main.accdb
    private const string Cursor1bMain = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";    // r-cursor-1b-main.accdb
    private const string VisAccdbFixture = "f51d3cf367ed40df9dbde48c82d6964ddf6002a3699121053347e1b385f9719a"; // r-q50-vis-accdb.accdb
    private const string VisMdbFixture = "b8be8fc1b5580799d437a24c6174ebd1713c0f137affce98a16e080e633e87f7";   // r-q50-vis-mdb.mdb

    private readonly List<string> _tempFiles = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<ConnectionObject> _connections = [];
    private readonly List<nint> _cells = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_1919, Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _connections.Count - 1; i >= 0; i--)
        {
            if (IntValue(Ok(Get(_connections[i], "State"))) == 1)
            {
                _ = Call(_connections[i], "Close");
            }
        }
        for (int i = _databases.Count - 1; i >= 0; i--)
        {
            if (!_databases[i].Closed)
            {
                _ = Call(_databases[i], "Close");
            }
        }
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i].Ptr);
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr && v->Pointer != 0)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free((void*)cell);
        }
        foreach (string f in _tempFiles)
        {
            TryDelete(f);
            TryDelete(Path.ChangeExtension(f, ".laccdb"));
            TryDelete(Path.ChangeExtension(f, ".ldb"));
        }
    }

    [Fact]
    public void Dao_named_repeated_and_positional_collision_returns_3001_and_recordset_unknown_param_records_3265()
    {
        // evidence/r-q2-cursor-dao/named-dao-refusals #37, #50, #52; named-dao-order-variants #45
        DatabaseObject db = OpenDao(Q2CursorDaoMain);

        // #37: db.OpenRecordset("T", Type:=4, TYPE:=2) -> 3001 DAO.Database with IErrorInfo & DBEngine.Errors.
        int dispId = GetMemberDispId(db, "OpenRecordset", ["Type", "TYPE"], out int[] paramIds);
        Assert.Equal(1, paramIds[0]);
        Assert.Equal(1, paramIds[1]);

        NativeVariant* rgvarg = stackalloc NativeVariant[3];
        rgvarg[0] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 2 }; // named TYPE=2
        rgvarg[1] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 4 }; // named Type=4
        nint bstrT = Marshal.StringToBSTR("T");
        rgvarg[2] = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstrT }; // positional Name="T"
        int* namedIds = stackalloc int[2];
        namedIds[0] = 1;
        namedIds[1] = 1;
        var dp = new DispParams { Args = (nint)rgvarg, NamedArgs = (nint)namedIds, ArgCount = 3, NamedArgCount = 2 };
        NativeVariant res = default;
        ExcepInfo excep = default;
        uint argErr = 0;
        try
        {
            int hr = db.Invoke(dispId, (ushort)InvokeKind.Method, &dp, &res, &excep, &argErr);
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(unchecked((int)0x800A0BB9), excep.Scode);
            Assert.Equal("DAO.Database", ReadAndFreeBstr(ref excep.Source));
            Assert.Equal("Invalid argument.", ReadAndFreeBstr(ref excep.Description));
            Assert.Equal("jeterr40.chm", ReadAndFreeBstr(ref excep.HelpFile));
            Assert.Equal(5003001u, excep.HelpContext);

            Assert.Single(_engine.Errors);
            Assert.Equal(3001, _engine.Errors[0].Number);
            Assert.Equal("DAO.Database", _engine.Errors[0].Source);
            Assert.Equal("Invalid argument.", _engine.Errors[0].Description);
            Assert.True(_engine.Errors[0].PublishesErrorInfo);
        }
        finally
        {
            Marshal.FreeBSTR(bstrT);
        }

        // named-dao-order-variants #45: db.OpenRecordset("T", 2, Type:=4) -> positional+named collision -> 3001.
        _engine.Record(Failure.Dao(3376, "DAO.Database", "Table 'Q2NoSuchMarker' does not exist."));
        NativeVariant* rgvarg2 = stackalloc NativeVariant[3];
        rgvarg2[0] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 4 }; // named Type=4
        rgvarg2[1] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 2 }; // positional Type=2
        nint bstrT2 = Marshal.StringToBSTR("T");
        rgvarg2[2] = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstrT2 }; // positional Name="T"
        int singleNamed = 1;
        var dp2 = new DispParams { Args = (nint)rgvarg2, NamedArgs = (nint)(&singleNamed), ArgCount = 3, NamedArgCount = 1 };
        ExcepInfo excep2 = default;
        try
        {
            int hr = db.Invoke(dispId, (ushort)InvokeKind.Method, &dp2, &res, &excep2, &argErr);
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(unchecked((int)0x800A0BB9), excep2.Scode);
            Assert.Equal("DAO.Database", ReadAndFreeBstr(ref excep2.Source));
            Assert.Equal("Invalid argument.", ReadAndFreeBstr(ref excep2.Description));
            Assert.Equal("jeterr40.chm", ReadAndFreeBstr(ref excep2.HelpFile));
            Assert.Equal(5003001u, excep2.HelpContext);

            Assert.Single(_engine.Errors);
            Assert.Equal(3001, _engine.Errors[0].Number);
            Assert.Equal("DAO.Database", _engine.Errors[0].Source);
        }
        finally
        {
            Marshal.FreeBSTR(bstrT2);
        }

        // Neighbour refusal: repeated named arg on Database.Execute (2 args <= 2 declared) stays refused by name (E_NOTIMPL).
        int execDispId = GetMemberDispId(db, "Execute", ["Options", "OPTIONS"], out int[] execParamIds);
        Assert.Equal(1, execParamIds[0]);
        Assert.Equal(1, execParamIds[1]);
        NativeVariant* rgvargExec = stackalloc NativeVariant[2];
        rgvargExec[0] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 128 };
        rgvargExec[1] = new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 128 };
        var dpExec = new DispParams { Args = (nint)rgvargExec, NamedArgs = (nint)namedIds, ArgCount = 2, NamedArgCount = 2 };
        ExcepInfo excepExec = default;
        int hrExec = db.Invoke(execDispId, (ushort)InvokeKind.Method, &dpExec, &res, &excepExec, &argErr);
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrExec);
        Assert.Equal(HResults.E_NOTIMPL, excepExec.Scode);
        _ = ReadAndFreeBstr(ref excepExec.Source);
        _ = ReadAndFreeBstr(ref excepExec.Description);
        _ = ReadAndFreeBstr(ref excepExec.HelpFile);

        // named-dao-refusals #50/#52: rs.MoveNext(Rows:=1) -> GetIDsOfNames 0x80020006 + DBEngine.Errors 3265 DAO.Fields.
        CalcDb.Com.Dao.RecordsetObject rs = OpenTableRecordset(db, "T", 2);
        _engine.Record(Failure.Dao(3376, "DAO.Database", "Table 'Q2NoSuchMarker' does not exist."));
        nint mName = Marshal.StringToHGlobalUni("MoveNext");
        nint pName = Marshal.StringToHGlobalUni("Rows");
        char** names = stackalloc char*[2];
        names[0] = (char*)mName;
        names[1] = (char*)pName;
        int* ids = stackalloc int[2];
        try
        {
            int hr = rs.GetIds(names, 2, ids);
            Assert.Equal(HResults.DISP_E_UNKNOWNNAME, hr);
            Assert.NotEqual(-1, ids[0]);
            Assert.Equal(-1, ids[1]);
            Assert.Single(_engine.Errors);
            Assert.Equal(3265, _engine.Errors[0].Number);
            Assert.Equal("DAO.Fields", _engine.Errors[0].Source);
            Assert.Equal("Item not found in this collection.", _engine.Errors[0].Description);
        }
        finally
        {
            Marshal.FreeHGlobal(mName);
            Marshal.FreeHGlobal(pName);
            Ok(Call(rs, "Close"));
        }
    }

    [Fact]
    public void Unordered_select_snapshot_lazy_chunk_materialization_across_update_matches_record()
    {
        // evidence/r-q2-cursor-dao/pop-snapshot-select-noorder-big calls 35-90
        DatabaseObject db = OpenDao(Q2CursorDaoMain);
        string v100 = new('v', 100);

        SelectRecordsetObject rs = OpenSelect(db, "SELECT ID, V FROM Big", 4);
        Assert.Equal(1, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(v100, StrValue(Ok(FieldValue(rs, "V"))));

        // Intervening UPDATE publishes after row 0 was materialized at open, before rows 1..2047 are fetched.
        Ok(Call(db, "Execute", "UPDATE Big SET V = 'chg' WHERE ID IN (2, 1000, 2000)", 128));

        // Row 0 stays cached from open-time; Move(1) fetches rows 1..500 after the UPDATE (seeing ID=2 -> 'chg').
        Ok(Call(rs, "MoveFirst"));
        Assert.Equal(1, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(v100, StrValue(Ok(FieldValue(rs, "V"))));

        Ok(Call(rs, "Move", 1));
        Assert.Equal(2, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal("chg", StrValue(Ok(FieldValue(rs, "V"))));

        // Move(998) lands on ordinal 999 (ID=982, V=v100); Move(1000) lands on ordinal 1999 (ID=2000, V='chg').
        Ok(Call(rs, "Move", 998));
        Assert.Equal(982, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(v100, StrValue(Ok(FieldValue(rs, "V"))));

        Ok(Call(rs, "Move", 1000));
        Assert.Equal(2000, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal("chg", StrValue(Ok(FieldValue(rs, "V"))));

        // Updating row 1 (ID=1) AFTER it was already materialized does not change the cached snapshot value.
        Ok(Call(db, "Execute", "UPDATE Big SET V = 'late' WHERE ID = 1", 128));
        Ok(Call(rs, "MoveFirst"));
        Assert.Equal(1, IntValue(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(v100, StrValue(Ok(FieldValue(rs, "V"))));

        // Active Workspace overlay still refuses by name.
        WorkspaceObject ws = _engine.DefaultWorkspace;
        Ok(Call(ws, "BeginTrans"));
        Ok(Call(db, "Execute", "UPDATE Big SET V = 'ov' WHERE ID = 3", 128));
        CallResult refused = Call(rs, "MoveFirst");
        Assert.NotNull(refused.Failure);
        Assert.Equal(FailureKind.NotModelled, refused.Failure!.Kind);
        Assert.StartsWith("NotModelled.Edit.SnapshotAfterPublish:", refused.Failure.Description);
        Ok(Call(ws, "Rollback"));

        Ok(Call(rs, "Close"));
    }

    [Fact]
    public void Derived_openrecordset_sort_expression_forms_matches_record()
    {
        // evidence/r-cursor-1b/sort-expression-forms (calls 5-186)
        DatabaseObject db = OpenDao(Cursor1bMain, readOnly: true);

        AssertDerivedSortOrder(db, "N DESC", [8, 7, 6, 5, 4, 3, 2, 1]);
        AssertDerivedSortOrder(db, "S", [8, 4, 6, 5, 7, 1, 2, 3]);
        AssertDerivedSortOrder(db, "S DESC, N", [3, 2, 1, 7, 5, 6, 4, 8]);
        AssertDerivedSortOrder(db, "D", [8, 6, 5, 1, 2, 3, 7, 4]);
        AssertDerivedSortOrder(db, "D DESC", [4, 7, 3, 2, 5, 1, 8, 6]);
        AssertDerivedSortOrder(db, "ID DESC", [8, 7, 6, 5, 4, 3, 2, 1]);
        AssertDerivedSortOrder(db, "[S] ASC", [8, 4, 6, 5, 7, 1, 2, 3]);
        AssertDerivedSortOrder(db, "N DESC, ID DESC", [8, 7, 6, 5, 4, 3, 2, 1]);
        AssertDerivedSortOrder(db, "Q", [6, 4, 2, 7, 3, 1, 8, 5]);
        AssertDerivedSortOrder(db, "N + 1", [1, 2, 3, 5, 4, 6, 7, 8]);
        AssertDerivedSortOrder(db, "", [1, 2, 3, 4, 5, 6, 7, 8]);
        AssertDerivedSortOrder(db, "N ASC, N DESC", [1, 2, 3, 5, 4, 6, 7, 8]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ado_select_on_table_dropped_by_dao_fails_3011_while_catalog_stays_stale(bool isAccdb)
    {
        // evidence/r-q50-jk44-ddl-vis/vis-cross-ddl-accdb and vis-cross-ddl-mdb calls 29, 53-56, 124-147
        string ext = isAccdb ? ".accdb" : ".mdb";
        string sha = isAccdb ? VisAccdbFixture : VisMdbFixture;
        string path = CopyFixture(sha, ext);

        nint engPtr = 0;
        Assert.Equal(HResults.S_OK, DBEngineObject.Create(new HostBinding(0xDA0_1920, Standalone: true, 0, 0, 0, 0, 0), 0, ComIids.IDispatch, &engPtr));
        DBEngineObject eng = ComObject.Recover<DBEngineObject>(engPtr);
        _held.Add((eng, engPtr));

        DatabaseObject db = Recover<DatabaseObject>(Ok(Call(eng, "OpenDatabase", path, false, false)));
        _databases.Add(db);
        ConnectionObject cn = OpenAdoConnection(path);

        // Call 29-30: ADO creates CrossAdo and inserts one row.
        Ok(Call(cn, "Execute", "CREATE TABLE CrossAdo (ID LONG CONSTRAINT PK_CA PRIMARY KEY, X LONG)", MissingArg(), 129));
        Ok(Call(cn, "Execute", "INSERT INTO CrossAdo (ID, X) VALUES (1, 99)", MissingArg(), 129));

        // Calls 53-54: DAO refreshes cache & TableDefs, then call 124: DAO drops CrossAdo.
        Ok(Call(eng, "Idle", 8));
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        Ok(Call(tds, "Refresh"));
        Ok(Call(db, "Execute", "DROP TABLE CrossAdo", 128));

        // Call 132-135: ADO SELECT on dropped CrossAdo fails 0x80040E37 / 3011 / -543884569 while cn.Catalog stays stale.
        CallResult sel = Call(cn, "Execute", "SELECT ID, X FROM CrossAdo");
        Assert.NotNull(sel.Failure);
        Assert.Equal(AdoObject.HResultNoTable, sel.Failure!.Scode);
        Assert.Equal("Microsoft Access Database Engine", sel.Failure.Source);
        Assert.Equal(5003011, sel.Failure.HelpContext);
        Assert.Equal(AdoObject.IidCommand, sel.Failure.ErrorInfoIid);
        Assert.Equal(
            "The Microsoft Access database engine could not find the object 'CrossAdo'. Make sure the object exists and that you spell its name and the path name correctly. If 'CrossAdo' is not a local object, check your network connection or contact the server administrator.",
            sel.Failure.Description);

        DispatchObject errs = Recover<DispatchObject>(Ok(Get(cn, "Errors")));
        Assert.Equal(1, IntValue(Ok(Get(errs, "Count"))));
        DispatchObject err0 = Recover<DispatchObject>(Ok(Get(errs, "Item", 0)));
        Assert.Equal(AdoObject.HResultNoTable, IntValue(Ok(Get(err0, "Number"))));
        Assert.Equal(-543884569, IntValue(Ok(Get(err0, "NativeError"))));
        Assert.Equal("3011", StrValue(Ok(Get(err0, "SQLState"))));
        Assert.Equal("Microsoft Access Database Engine", StrValue(Ok(Get(err0, "Source"))));

        // Call 141-147: cn.OpenSchema(20) still lists CrossAdo because cn.Catalog remained stale.
        DispatchObject sch = Recover<DispatchObject>(Ok(Call(cn, "OpenSchema", 20)));
        var tableNames = new List<string>();
        while (!BoolValue(Ok(Get(sch, "EOF"))))
        {
            DispatchObject flds = Recover<DispatchObject>(Ok(Get(sch, "Fields")));
            DispatchObject nameFld = Recover<DispatchObject>(Ok(Get(flds, "Item", "TABLE_NAME")));
            tableNames.Add(StrValue(Ok(Get(nameFld, "Value"))));
            Ok(Call(sch, "MoveNext"));
        }
        Assert.Contains("CrossAdo", tableNames, StringComparer.OrdinalIgnoreCase);
        Ok(Call(sch, "Close"));
    }

    private void AssertDerivedSortOrder(DatabaseObject db, string sort, int[] expectedIds)
    {
        CalcDb.Com.Dao.RecordsetObject rs = OpenTableRecordset(db, "F", 2);
        Ok(Put(rs, "Sort", sort));
        SelectRecordsetObject rd = Recover<SelectRecordsetObject>(Ok(Call(rs, "OpenRecordset")));
        var actual = new List<int>();
        while (!BoolValue(Ok(Get(rd, "EOF"))))
        {
            actual.Add(IntValue(Ok(FieldValue(rd, "ID"))));
            Ok(Call(rd, "MoveNext"));
        }
        Assert.Equal(expectedIds, actual);
        Ok(Call(rd, "Close"));
        Ok(Call(rs, "Close"));
    }

    private string CopyFixture(string sha, string ext)
    {
        ArtifactStore store = ArtifactStore.ForRepository(DeclarationGenerationTests.Root());
        string path = Path.Combine(Path.GetTempPath(), $"calcdb-w19-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, store.ReadAll(sha));
        _tempFiles.Add(path);
        return path;
    }

    private DatabaseObject OpenDao(string sha, bool readOnly = false)
    {
        string path = CopyFixture(sha, ".accdb");
        DatabaseObject db = Recover<DatabaseObject>(Ok(_engine.DefaultWorkspace.OpenDatabase([Cell(path), Cell(false), Cell(readOnly)])));
        _databases.Add(db);
        return db;
    }

    private ConnectionObject OpenAdoConnection(string path)
    {
        nint cnPtr = 0;
        Assert.Equal(HResults.S_OK, ConnectionObject.Create(new HostBinding(0xAD0_1919, Standalone: true, 0, 0, 0, 0, 0), 0, ComIids.IDispatch, &cnPtr));
        ConnectionObject cn = ComObject.Recover<ConnectionObject>(cnPtr);
        _held.Add((cn, cnPtr));
        _connections.Add(cn);
        Ok(Put(cn, "Mode", 3));
        Ok(Call(cn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path};"));
        return cn;
    }

    private CalcDb.Com.Dao.RecordsetObject OpenTableRecordset(DatabaseObject db, string table, int type) =>
        Recover<CalcDb.Com.Dao.RecordsetObject>(Ok(Call(db, "OpenRecordset", table, type)));

    private SelectRecordsetObject OpenSelect(DatabaseObject db, string sql, int type) =>
        Recover<SelectRecordsetObject>(Ok(Call(db, "OpenRecordset", sql, type)));

    private CallResult FieldValue(SelectRecordsetObject rs, string name)
    {
        SelectFieldsObject fields = Recover<SelectFieldsObject>(Ok(Get(rs, "Fields")));
        SelectFieldObject field = Recover<SelectFieldObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private int GetMemberDispId(DispatchObject target, string member, string[] paramNames, out int[] paramIds)
    {
        int total = 1 + paramNames.Length;
        char** names = stackalloc char*[total];
        nint[] allocs = new nint[total];
        allocs[0] = Marshal.StringToHGlobalUni(member);
        names[0] = (char*)allocs[0];
        for (int i = 0; i < paramNames.Length; i++)
        {
            allocs[i + 1] = Marshal.StringToHGlobalUni(paramNames[i]);
            names[i + 1] = (char*)allocs[i + 1];
        }
        int* ids = stackalloc int[total];
        try
        {
            int hr = target.GetIds(names, (uint)total, ids);
            Assert.Equal(HResults.S_OK, hr);
            paramIds = new int[paramNames.Length];
            for (int i = 0; i < paramNames.Length; i++)
            {
                paramIds[i] = ids[i + 1];
            }
            return ids[0];
        }
        finally
        {
            for (int i = 0; i < total; i++)
            {
                Marshal.FreeHGlobal(allocs[i]);
            }
        }
    }

    private static string ReadAndFreeBstr(ref nint bstr)
    {
        if (bstr == 0)
        {
            return "";
        }
        string s = Marshal.PtrToStringBSTR(bstr);
        Marshal.FreeBSTR(bstr);
        bstr = 0;
        return s;
    }

    private CallResult Call(DispatchObject target, string name, params object?[] args) =>
        Invoke(target, name, InvokeKind.Method, args);

    private CallResult Get(DispatchObject target, string name, params object?[] args) =>
        Invoke(target, name, InvokeKind.PropertyGet, args);

    private CallResult Put(DispatchObject target, string name, params object?[] args) =>
        Invoke(target, name, InvokeKind.PropertyPut, args);

    private CallResult Invoke(DispatchObject target, string name, InvokeKind kind, object?[] args)
    {
        MemberDecl? member = target.Declaration.Members.FirstOrDefault(m => m.Kind == kind && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(member);
        Span<nint> cells = stackalloc nint[member!.Parameters.Count];
        for (int i = 0; i < args.Length; i++)
        {
            cells[i] = args[i] is MissingSentinel ? CellMissing() : Cell(args[i]);
        }
        return target.Run(member, cells);
    }

    private sealed class MissingSentinel;
    private static readonly MissingSentinel s_missing = new();
    private static object MissingArg() => s_missing;

    private nint CellMissing()
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed(1, (nuint)sizeof(NativeVariant));
        v->Vt = (ushort)VarType.Error;
        v->Int64 = unchecked((int)0x80020004); // DISP_E_PARAMNOTFOUND
        _cells.Add((nint)v);
        return (nint)v;
    }

    private nint Cell(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed(1, (nuint)sizeof(NativeVariant));
        switch (value)
        {
            case null:
                v->Vt = (ushort)VarType.Null;
                break;
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
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.Dispatch, result.Failure?.Description);
        T obj = ComObject.Recover<T>(result.Scalar.Pointer);
        _held.Add((obj, result.Scalar.Pointer));
        return obj;
    }

    private static CallResult Ok(CallResult result)
    {
        Assert.Null(result.Failure);
        return result;
    }

    private static int IntValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.I4, (VarType)v.Vt);
        int value = unchecked((int)v.Int64);
        result.ReleaseUntaken();
        return value;
    }

    private static bool BoolValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bool, (VarType)v.Vt);
        bool value = (short)v.Int64 != 0;
        result.ReleaseUntaken();
        return value;
    }

    private static string StrValue(CallResult result)
    {
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal(VarType.Bstr, (VarType)v.Vt);
        string s = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return s;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
    }
}
