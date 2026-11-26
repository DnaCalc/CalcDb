using System.Runtime.InteropServices;

using CalcDb.Com.Ado;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// End-to-end COM DAO and ADO tests for <c>SELECT ... INTO</c> (MakeTable; R-SQL-2 SQL-ACT-MAKE;
/// <c>evidence/r-q52-ddl-make-domain/maketable-*</c>, <c>evidence/r-q47-bigint-expr-edges/bi-select-into-accdb</c>,
/// <c>evidence/r-q49-dt2-draftprops/dt2-select-into-accdb</c>).
/// </summary>
public sealed unsafe class MakeTableTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbOpenSnapshot = 4;

    private static long s_nextHost = 0xDA0_A452;
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-maketable-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(checked((nuint)Interlocked.Increment(ref s_nextHost)), Standalone: true, 0, 0, 0, 0, 0));

    public void Dispose()
    {
        for (int i = _held.Count - 1; i >= 0; i--)
        {
            Marshal.Release(_held[i]);
        }
        foreach (nint cell in _cells)
        {
            var v = (NativeVariant*)cell;
            if ((VarType)v->Vt == VarType.Bstr)
            {
                Marshal.FreeBSTR(v->Pointer);
            }
            NativeMemory.Free((void*)cell);
        }
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private DBEngineObject CreateDaoEngine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private ConnectionObject CreateAdoConnection()
    {
        nint conn;
        Assert.Equal(0, ConnectionObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &conn));
        _held.Add(conn);
        return ComObject.Recover<ConnectionObject>(conn);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyGet), [.. args.Select(Arg)]);

    private CallResult Put(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyPut), [.. args.Select(Arg)]);

    private CallResult Call(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.Method), [.. args.Select(Arg)]);

    private T Unwrap<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint ptr = result.OwnsScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        Assert.NotEqual(0, ptr);
        _held.Add(ptr);
        T obj = ComObject.Recover<T>(ptr);
        _ptrs[obj] = ptr;
        return obj;
    }

    private static int Int32Of(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        int val = (VarType)v.Vt switch
        {
            VarType.I2 => (short)v.Int64,
            VarType.I4 => (int)v.Int64,
            _ => throw new Xunit.Sdk.XunitException($"Expected I2/I4, got {(VarType)v.Vt}"),
        };
        result.ReleaseUntaken();
        return val;
    }

    private static string BstrOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal((ushort)VarType.Bstr, v.Vt);
        string text = Marshal.PtrToStringBSTR(v.Pointer);
        result.ReleaseUntaken();
        return text;
    }

    private DatabaseObject CreateSeededDatabase(DBEngineObject eng, string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DatabaseObject db = Unwrap<DatabaseObject>(Call(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Call(db, "Execute", "CREATE TABLE Items (ID COUNTER CONSTRAINT PK_Items PRIMARY KEY, Cat TEXT(20), Name TEXT(30), Qty LONG, Price CURRENCY, Rate DOUBLE, Dt DATETIME, Active YESNO, Descr MEMO)", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "CREATE TABLE Groups (Cat TEXT(20), Label TEXT(20))", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "CREATE TABLE EmptyTbl (ID LONG, V LONG, S TEXT(20))", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Groups VALUES ('A', 'Group A')", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Groups VALUES ('B', 'Group B')", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Groups VALUES ('C', 'Group C')", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('A', 'Alpha', 10, 12.5, 1.5, #2026-01-10#, True, 'first-note')", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('A', 'Beta', 20, 25.0, 2.5, #2026-02-15#, False, Null)", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO Items (Cat, Name, Qty, Price, Rate, Dt, Active, Descr) VALUES ('B', 'Delta', 30, 5.25, -0.5, #2026-03-20#, True, 'b-one')", DbFailOnError).Failure);
        return db;
    }

    [Theory]
    [InlineData("basic-accdb.accdb", DbVersion120)]
    [InlineData("basic-mdb.mdb", DbVersion40)]
    public void Dao_maketable_basic_preserves_counter_schema_and_empty_tables(string fileName, int version)
    {
        // Replays r-q52-ddl-make-domain/maketable-basic-dao-accdb and maketable-basic-dao-mdb:
        // populated (COUNTER->Attributes 17, MEMO, 0 indexes copied), empty source, and WHERE 1=0.
        DBEngineObject eng = CreateDaoEngine();
        DatabaseObject db = CreateSeededDatabase(eng, fileName, version);

        Assert.Null(Call(db, "Execute", "SELECT * INTO MT_Pop FROM Items", DbFailOnError).Failure);
        Assert.Equal(3, Int32Of(Get(db, "RecordsAffected")));

        Assert.Null(Call(db, "Execute", "SELECT * INTO MT_Empty FROM EmptyTbl", DbFailOnError).Failure);
        Assert.Equal(0, Int32Of(Get(db, "RecordsAffected")));

        Assert.Null(Call(db, "Execute", "SELECT ID, Cat, Qty INTO MT_Zero FROM Items WHERE 1 = 0", DbFailOnError).Failure);
        Assert.Equal(0, Int32Of(Get(db, "RecordsAffected")));

        DispatchObject tds = Unwrap<DispatchObject>(Get(db, "TableDefs"));
        Assert.Null(Call(tds, "Refresh").Failure);

        TableDefObject pop = Unwrap<TableDefObject>(Get(tds, "Item", "MT_Pop"));
        DispatchObject popFields = Unwrap<DispatchObject>(Get(pop, "Fields"));
        Assert.Equal(9, Int32Of(Get(popFields, "Count")));

        DispatchObject idField = Unwrap<DispatchObject>(Get(popFields, "Item", 0));
        Assert.Equal("ID", BstrOf(Get(idField, "Name")));
        Assert.Equal(4, Int32Of(Get(idField, "Type")));
        Assert.Equal(4, Int32Of(Get(idField, "Size")));
        Assert.Equal(17, Int32Of(Get(idField, "Attributes")));

        DispatchObject popIdx = Unwrap<DispatchObject>(Get(pop, "Indexes"));
        Assert.Equal(0, Int32Of(Get(popIdx, "Count")));

        TableDefObject emptyTd = Unwrap<TableDefObject>(Get(tds, "Item", "MT_Empty"));
        DispatchObject emptyFields = Unwrap<DispatchObject>(Get(emptyTd, "Fields"));
        Assert.Equal(3, Int32Of(Get(emptyFields, "Count")));
        DispatchObject emptyId = Unwrap<DispatchObject>(Get(emptyFields, "Item", 0));
        Assert.Equal(1, Int32Of(Get(emptyId, "Attributes")));

        Assert.Null(Call(db, "Close").Failure);
    }

    [Theory]
    [InlineData("expr-accdb.accdb", DbVersion120)]
    [InlineData("expr-mdb.mdb", DbVersion40)]
    public void Dao_maketable_expressions_and_aggregates_match_field_metadata(string fileName, int version)
    {
        // Replays r-q52-ddl-make-domain/maketable-expr-agg-dao-accdb and maketable-expr-agg-dao-mdb:
        // computed expressions (variable layout Attributes=2 for Long/Decimal/Currency/Text(255)/VarBinary(510),
        // fixed Attributes=1 for Integer comparison) and LEFT JOIN + GROUP BY aggregates.
        DBEngineObject eng = CreateDaoEngine();
        DatabaseObject db = CreateSeededDatabase(eng, fileName, version);

        // Populated MakeTable with variable-layout computed scalars refuses cleanly before creating the table
        // until CalcDb.Isam.FieldEncoder supports variable-flagged Long/Currency/Double/DateTime/Numeric.
        CallResult popExpr = Call(db, "Execute",
            "SELECT ID, Qty * 2 AS DblQty INTO MT_ExprPop FROM Items WHERE Qty IS NOT NULL",
            DbFailOnError);
        Assert.NotNull(popExpr.Failure);
        Assert.Equal(FailureKind.NotModelled, popExpr.Failure!.Kind);

        // Empty (WHERE 1 = 0) MakeTable creates the exact ACE schema including Attributes=2 for computed 4+B scalars
        // and Attributes=1 for Integer comparisons (maketable-expr-agg-dao-{accdb,mdb}).
        Assert.Null(Call(db, "Execute",
            "SELECT ID, Qty * 2 AS DblQty, Qty * 1.5 AS FltVal, Price + 1 AS NewPrice, Cat & '-' & Name AS Tag, Null AS NullCol, CCur(Qty) AS CyQty, Qty > 15 AS BigFlag, IIf(Active, 'Y', 'N') AS ActStr INTO MT_Expr FROM Items WHERE 1 = 0",
            DbFailOnError).Failure);
        Assert.Equal(0, Int32Of(Get(db, "RecordsAffected")));

        Assert.Null(Call(db, "Execute",
            "SELECT G.Cat, G.Label, COUNT(*) AS CntAll, COUNT(I.Qty) AS CntQty, SUM(I.Qty) AS SumQty, AVG(I.Price) AS AvgPrice, MIN(I.Dt) AS MinDt, MAX(I.Rate) AS MaxRate INTO MT_Agg FROM Groups AS G LEFT JOIN Items AS I ON G.Cat = I.Cat WHERE 1 = 0 GROUP BY G.Cat, G.Label ORDER BY G.Cat",
            DbFailOnError).Failure);
        Assert.Equal(0, Int32Of(Get(db, "RecordsAffected")));

        DispatchObject tds = Unwrap<DispatchObject>(Get(db, "TableDefs"));
        Assert.Null(Call(tds, "Refresh").Failure);

        // MT_ExprPop must not exist after pre-create NotModelled refusal.
        CallResult missingPop = Get(tds, "Item", "MT_ExprPop");
        Assert.NotNull(missingPop.Failure);
        Assert.Equal(3265, missingPop.Failure!.Number);

        TableDefObject exprTd = Unwrap<TableDefObject>(Get(tds, "Item", "MT_Expr"));
        DispatchObject exprFields = Unwrap<DispatchObject>(Get(exprTd, "Fields"));
        Assert.Equal(9, Int32Of(Get(exprFields, "Count")));

        // (Name, Type, Size, Attributes) from maketable-expr-agg-dao-{accdb,mdb}
        (string Name, int Type, int Size, int Attr)[] expectedExpr =
        [
            ("ID", 4, 4, 17),
            ("DblQty", 4, 4, 2),
            ("FltVal", 20, 16, 2),
            ("NewPrice", 5, 8, 2),
            ("Tag", 10, 255, 2),
            ("NullCol", 9, 510, 2),
            ("CyQty", 5, 8, 2),
            ("BigFlag", 3, 2, 1),
            ("ActStr", 10, 255, 2),
        ];
        for (int i = 0; i < expectedExpr.Length; i++)
        {
            DispatchObject f = Unwrap<DispatchObject>(Get(exprFields, "Item", i));
            Assert.Equal(expectedExpr[i].Name, BstrOf(Get(f, "Name")));
            Assert.Equal(expectedExpr[i].Type, Int32Of(Get(f, "Type")));
            Assert.Equal(expectedExpr[i].Size, Int32Of(Get(f, "Size")));
            Assert.Equal(expectedExpr[i].Attr, Int32Of(Get(f, "Attributes")));
        }

        TableDefObject aggTd = Unwrap<TableDefObject>(Get(tds, "Item", "MT_Agg"));
        DispatchObject aggFields = Unwrap<DispatchObject>(Get(aggTd, "Fields"));
        Assert.Equal(8, Int32Of(Get(aggFields, "Count")));
        (string Name, int Type, int Size, int Attr)[] expectedAgg =
        [
            ("Cat", 10, 20, 2),
            ("Label", 10, 20, 2),
            ("CntAll", 4, 4, 2),
            ("CntQty", 4, 4, 2),
            ("SumQty", 7, 8, 2),
            ("AvgPrice", 5, 8, 2),
            ("MinDt", 8, 8, 2),
            ("MaxRate", 7, 8, 2),
        ];
        for (int i = 0; i < expectedAgg.Length; i++)
        {
            DispatchObject f = Unwrap<DispatchObject>(Get(aggFields, "Item", i));
            Assert.Equal(expectedAgg[i].Name, BstrOf(Get(f, "Name")));
            Assert.Equal(expectedAgg[i].Type, Int32Of(Get(f, "Type")));
            Assert.Equal(expectedAgg[i].Size, Int32Of(Get(f, "Size")));
            Assert.Equal(expectedAgg[i].Attr, Int32Of(Get(f, "Attributes")));
        }

        Assert.Null(Call(db, "Close").Failure);
    }

    [Theory]
    [InlineData("err-accdb.accdb", DbVersion120)]
    [InlineData("err-mdb.mdb", DbVersion40)]
    public void Dao_maketable_errors_match_recorded_codes(string fileName, int version)
    {
        // Replays r-q52-ddl-make-domain/maketable-errors-dao-accdb and maketable-errors-dao-mdb:
        // 3010 (exact & case-insensitive), 3078, 3191, 3125, and 3027 (read-only).
        DBEngineObject eng = CreateDaoEngine();
        DatabaseObject db = CreateSeededDatabase(eng, fileName, version);
        string path = Path.Combine(_dir, fileName);

        CallResult r1 = Call(db, "Execute", "SELECT ID, Cat INTO Groups FROM Items", DbFailOnError);
        Assert.NotNull(r1.Failure);
        Assert.Equal(3010, r1.Failure!.Number);
        Assert.Equal("Table 'Groups' already exists.", r1.Failure.Description);

        CallResult r2 = Call(db, "Execute", "SELECT ID INTO groups FROM Items", DbFailOnError);
        Assert.NotNull(r2.Failure);
        Assert.Equal(3010, r2.Failure!.Number);
        Assert.Equal("Table 'groups' already exists.", r2.Failure.Description);

        CallResult r3 = Call(db, "Execute", "SELECT * INTO MT_Miss FROM NoSuchTable", DbFailOnError);
        Assert.NotNull(r3.Failure);
        Assert.Equal(3078, r3.Failure!.Number);

        CallResult r4 = Call(db, "Execute", "SELECT ID, Qty AS ID INTO MT_DupCol FROM Items", DbFailOnError);
        Assert.NotNull(r4.Failure);
        Assert.Equal(3191, r4.Failure!.Number);
        Assert.Equal("Cannot define field more than once.", r4.Failure.Description);

        CallResult r5 = Call(db, "Execute", "SELECT ID INTO [Bad.Name] FROM Items", DbFailOnError);
        Assert.NotNull(r5.Failure);
        Assert.Equal(3125, r5.Failure!.Number);

        Assert.Null(Call(db, "Close").Failure);

        DatabaseObject dbRo = Unwrap<DatabaseObject>(Call(eng, "OpenDatabase", path, false, true));
        CallResult r6 = Call(dbRo, "Execute", "SELECT ID INTO MT_Ro FROM Items", DbFailOnError);
        Assert.NotNull(r6.Failure);
        Assert.Equal(3027, r6.Failure!.Number);
        Assert.Equal("Cannot update. Database or object is read-only.", r6.Failure.Description);
        Assert.Null(Call(dbRo, "Close").Failure);
    }

    [Theory]
    [InlineData("ado-accdb.accdb", DbVersion120)]
    [InlineData("ado-mdb.mdb", DbVersion40)]
    public void Ado_maketable_executes_and_maps_3010(string fileName, int version)
    {
        // Replays r-q52-ddl-make-domain/maketable-ado-accdb and maketable-ado-mdb:
        // Connection.Execute with adCmdText|adExecuteNoRecords (129) and ByRef RecordsAffected, plus 3010 (0x80040E14).
        DBEngineObject eng = CreateDaoEngine();
        DatabaseObject seed = CreateSeededDatabase(eng, fileName, version);
        Assert.Null(Call(seed, "Close").Failure);
        string path = Path.Combine(_dir, fileName);

        ConnectionObject cn = CreateAdoConnection();
        Assert.Null(Put(cn, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
        Assert.Null(Put(cn, "Mode", 3).Failure);
        Assert.Null(Call(cn, "Open", path, "", "", -1).Failure);

        NativeVariant affected = new() { Vt = (ushort)VarType.I4, Int64 = unchecked((uint)-1) };
        NativeVariant byRefArg = new() { Vt = (ushort)(VarType.Variant | VarType.ByRef), Pointer = (nint)(&affected) };
        CallResult ok1 = cn.Run(cn.Declaration.Member("Execute", InvokeKind.Method), [Arg("SELECT ID, Cat, Qty INTO MT_AdoPop FROM Items WHERE Qty IS NOT NULL"), (nint)(&byRefArg), Arg(129)]);
        Assert.Null(ok1.Failure);
        Assert.Equal(3, (int)affected.Int64);

        affected = new() { Vt = (ushort)VarType.I4, Int64 = unchecked((uint)-1) };
        CallResult ok2 = cn.Run(cn.Declaration.Member("Execute", InvokeKind.Method), [Arg("SELECT * INTO MT_AdoEmpty FROM EmptyTbl"), (nint)(&byRefArg), Arg(129)]);
        Assert.Null(ok2.Failure);
        Assert.Equal(0, (int)affected.Int64);

        affected = new() { Vt = (ushort)VarType.I4, Int64 = unchecked((uint)-1) };
        CallResult err3010 = cn.Run(cn.Declaration.Member("Execute", InvokeKind.Method), [Arg("SELECT ID INTO Groups FROM Items"), (nint)(&byRefArg), Arg(129)]);
        Assert.NotNull(err3010.Failure);
        Assert.Equal(unchecked((int)0x80040E14), err3010.Failure!.Scode);
        Assert.Equal("Table 'Groups' already exists.", err3010.Failure.Description);
        Assert.Equal(-1, (int)affected.Int64);

        Assert.Null(Call(cn, "Close").Failure);
    }

    [Fact]
    public void Dao_maketable_bigint_and_datetime2_preserve_types_and_abort_on_row_error()
    {
        // Replays r-q47-bigint-expr-edges/bi-select-into-accdb and r-q49-dt2-draftprops/dt2-select-into-accdb.
        DBEngineObject eng = CreateDaoEngine();
        string path = Path.Combine(_dir, "ext-maketable.accdb");
        DatabaseObject db = Unwrap<DatabaseObject>(Call(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));

        Assert.Null(Call(db, "Execute", "CREATE TABLE TBi (ID LONG CONSTRAINT PK_TBi PRIMARY KEY, B BIGINT)", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO TBi VALUES (1, 10)", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO TBi VALUES (2, Null)", DbFailOnError).Failure);

        Assert.Null(Call(db, "Execute", "SELECT ID, B, B + 1 AS BAdd, B / 2 AS BDiv, B \\ 2 AS BIdiv, CStr(B) AS BStr INTO TOutBi FROM TBi WHERE ID = 1 ORDER BY ID", DbFailOnError).Failure);
        Assert.Equal(1, Int32Of(Get(db, "RecordsAffected")));

        CallResult err94 = Call(db, "Execute", "SELECT ID, CStr(B) AS BStr INTO TOutErr FROM TBi WHERE ID = 2", DbFailOnError);
        Assert.NotNull(err94.Failure);
        Assert.Equal(94, err94.Failure!.Number);

        Assert.Null(Call(db, "Execute", "CREATE TABLE TDt2 (ID LONG CONSTRAINT PK_TDt2 PRIMARY KEY, D DATETIME2)", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "INSERT INTO TDt2 VALUES (1, '2026-03-05 13:45:01.1234567')", DbFailOnError).Failure);
        Assert.Null(Call(db, "Execute", "SELECT ID, D INTO TOutDt2 FROM TDt2 ORDER BY ID", DbFailOnError).Failure);
        Assert.Equal(1, Int32Of(Get(db, "RecordsAffected")));

        DispatchObject tds = Unwrap<DispatchObject>(Get(db, "TableDefs"));
        Assert.Null(Call(tds, "Refresh").Failure);

        // TOutErr must not exist after row-level error 94
        CallResult missing = Get(tds, "Item", "TOutErr");
        Assert.NotNull(missing.Failure);
        Assert.Equal(3265, missing.Failure!.Number);

        TableDefObject biTd = Unwrap<TableDefObject>(Get(tds, "Item", "TOutBi"));
        DispatchObject biFields = Unwrap<DispatchObject>(Get(biTd, "Fields"));
        Assert.Equal(16, Int32Of(Get(Unwrap<DispatchObject>(Get(biFields, "Item", 1)), "Type")));
        Assert.Equal(16, Int32Of(Get(Unwrap<DispatchObject>(Get(biFields, "Item", 2)), "Type")));
        Assert.Equal(10, Int32Of(Get(Unwrap<DispatchObject>(Get(biFields, "Item", 5)), "Type")));
        Assert.Equal(255, Int32Of(Get(Unwrap<DispatchObject>(Get(biFields, "Item", 5)), "Size")));

        TableDefObject dt2Td = Unwrap<TableDefObject>(Get(tds, "Item", "TOutDt2"));
        DispatchObject dt2Fields = Unwrap<DispatchObject>(Get(dt2Td, "Fields"));
        DispatchObject dField = Unwrap<DispatchObject>(Get(dt2Fields, "Item", 1));
        Assert.Equal(26, Int32Of(Get(dField, "Type")));
        Assert.Equal(42, Int32Of(Get(dField, "Size")));
        Assert.Equal(1, Int32Of(Get(dField, "Attributes")));

        Assert.Null(Call(db, "Close").Failure);
    }
}
