using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// End-to-end COM DAO &amp; SQL tests for <c>BIGINT</c> (<c>0x13</c>) and <c>DATETIME2</c> (<c>0x14</c>)
/// (<c>EXT-READ</c>; <c>evidence/r-q47-bigint-expr-edges/bi-*</c>, <c>evidence/r-q48-ado-batch-edits/bi-agg-*</c>,
/// <c>evidence/r-q49-dt2-draftprops/dt2-*</c>, <c>evidence/r-q-fmt-sort/dt2-ticks-accdb</c>).
/// </summary>
public sealed unsafe class DaoBigIntDateTime2Tests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbOpenTable = 1;
    private const int DbOpenDynaset = 2;
    private const int DbOpenSnapshot = 4;

    private static long s_nextHost = 0xDA0_B1D2;
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-bi-dt2-").FullName;
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

    private DBEngineObject CreateEngine()
    {
        nint engine;
        Assert.Equal(0, DBEngineObject.Create(_engine.Binding, 0, NativeRoute.IidIDispatch, &engine));
        _held.Add(engine);
        return ComObject.Recover<DBEngineObject>(engine);
    }

    private nint Arg(object? value)
    {
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case long l: v->Vt = (ushort)VarType.I8; v->Int64 = l; break;
            case double d: v->Vt = (ushort)VarType.R8; v->Double = d; break;
            case DateTime dt: v->Vt = (ushort)VarType.Date; v->Double = dt.ToOADate(); break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
            case DBNull: v->Vt = (ushort)VarType.Null; break;
            case DispatchObject obj:
                nint ptr = _ptrs[obj];
                Marshal.AddRef(ptr);
                _held.Add(ptr);
                v->Vt = (ushort)VarType.Dispatch;
                v->Pointer = ptr;
                break;
        }
        _cells.Add((nint)v);
        return (nint)v;
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyGet), [.. args.Select(Arg)]);

    private CallResult Put(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.PropertyPut), [.. args.Select(Arg)]);

    private CallResult Method(DispatchObject target, string member, params object?[] args) =>
        target.Run(target.Declaration.Member(member, InvokeKind.Method), [.. args.Select(Arg)]);

    private T Recover<T>(CallResult result) where T : ComObject
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint p = result.OwnsScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        _held.Add(p);
        T obj = ComObject.Recover<T>(p);
        _ptrs[obj] = p;
        return obj;
    }

    private static NativeVariant ReadVariant(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        return v;
    }

    private static int IntOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        int v = (int)(result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64);
        result.ReleaseUntaken();
        return v;
    }

    private static long LongOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal((ushort)VarType.I8, v.Vt);
        long value = v.Int64;
        result.ReleaseUntaken();
        return value;
    }

    private static double DoubleOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        NativeVariant v = result.HasScalar ? result.Scalar : *result.Value!.Pointer;
        Assert.Equal((ushort)VarType.R8, v.Vt);
        double value = v.Double;
        result.ReleaseUntaken();
        return value;
    }

    private static bool BoolOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        bool v = (result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64) != 0;
        result.ReleaseUntaken();
        return v;
    }

    private static string TextOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint ptr = result.HasScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        string s = Marshal.PtrToStringBSTR(ptr);
        result.ReleaseUntaken();
        return s;
    }

    private static bool IsNullResult(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        ushort vt = result.HasScalar ? result.Scalar.Vt : result.Value!.Pointer->Vt;
        result.ReleaseUntaken();
        return vt == (ushort)VarType.Null;
    }

    private Failure? FirstCellFailure(DatabaseObject db, string sql)
    {
        CallResult open = Method(db, "OpenRecordset", sql, DbOpenSnapshot);
        if (open.Failure is not null)
        {
            return open.Failure;
        }
        SelectRecordsetObject q = Recover<SelectRecordsetObject>(open);
        SelectFieldObject f = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(q, "Fields")), "Item", 0));
        Failure? fail = Get(f, "Value").Failure;
        Assert.Null(Method(q, "Close").Failure);
        return fail;
    }

    [Fact]
    public void BigInt_dao_createfield_runtime_arithmetic_aggregates_and_parameters()
    {
        string path = Path.Combine(_dir, "bigint.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(created, "Close").Failure);

        // DAO TableDef.CreateField("B", 16) upgrades page 0 format code to 0x05 ("16.7")
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        NewTableDefObject td = Recover<NewTableDefObject>(Method(db, "CreateTableDef", "T"));
        NewFieldsObject tfs = Recover<NewFieldsObject>(Get(td, "Fields"));
        NewFieldObject fId = Recover<NewFieldObject>(Method(td, "CreateField", "ID", 4));
        NewFieldObject fB = Recover<NewFieldObject>(Method(td, "CreateField", "B", 16));
        Assert.Equal(19, IntOf(Get(fB, "Type")));
        Assert.Equal(8, IntOf(Get(fB, "Size")));
        Assert.Equal(0, IntOf(Get(fB, "Attributes")));
        Assert.Null(Method(tfs, "Append", fId).Failure);
        Assert.Null(Method(tfs, "Append", fB).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        Assert.Null(Method(tds, "Append", td).Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Equal("16.7", TextOf(Get(db2, "Version")));
        DefinitionCollection tds2 = Recover<DefinitionCollection>(Get(db2, "TableDefs"));
        TableDefObject td2 = Recover<TableDefObject>(Get(tds2, "Item", "T"));
        DefinitionCollection fls2 = Recover<DefinitionCollection>(Get(td2, "Fields"));
        DefinitionFieldObject rB = Recover<DefinitionFieldObject>(Get(fls2, "Item", "B"));
        Assert.Equal(16, IntOf(Get(rB, "Type")));
        Assert.Equal(8, IntOf(Get(rB, "Size")));
        Assert.Equal(1, IntOf(Get(rB, "Attributes")));

        // CREATE INDEX DESC before inserting rows (matching evidence/r-q47-bigint-expr-edges/bi-index-keys-accdb)
        Assert.Null(Method(db2, "Execute", "CREATE INDEX IX_B_DESC ON T (B DESC)", DbFailOnError).Failure);

        // DAO AddNew with 64-bit extremes, Null, and out-of-range refusal (3421)
        RecordsetObject rs = Recover<RecordsetObject>(Method(db2, "OpenRecordset", "T", DbOpenTable));
        FieldsObject rsF = Recover<FieldsObject>(Get(rs, "Fields"));
        FieldObject fIdRs = Recover<FieldObject>(Get(rsF, "Item", "ID"));
        FieldObject fBRs = Recover<FieldObject>(Get(rsF, "Item", "B"));

        void InsertRow(int id, object? b)
        {
            Assert.Null(Method(rs, "AddNew").Failure);
            Assert.Null(Put(fIdRs, "Value", id).Failure);
            Assert.Null(Put(fBRs, "Value", b).Failure);
            Assert.Null(Method(rs, "Update").Failure);
        }

        InsertRow(1, long.MinValue);
        InsertRow(2, -5L);
        InsertRow(3, 5L);
        InsertRow(4, long.MaxValue);
        InsertRow(5, DBNull.Value);

        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(fIdRs, "Value", 6).Failure);
        Assert.Equal(3421, Put(fBRs, "Value", "9223372036854775808").Failure?.Number);
        Assert.Null(Method(rs, "CancelUpdate").Failure);
        Assert.Null(Method(rs, "Close").Failure);

        // SQL INSERT literal overflow (3464) & UPDATE arithmetic overflow (6)
        Assert.Equal(3464, Method(db2, "Execute", "INSERT INTO T VALUES (7, 9223372036854775808)", DbFailOnError).Failure?.Number);
        Assert.Equal(6, Method(db2, "Execute", "UPDATE T SET B = B + 1 WHERE ID = 4", DbFailOnError).Failure?.Number);

        // Exact 64-bit half-to-even division, Mod, VarType=20, TypeName="LongLong", IsNumeric=-1, CStr(Null)->94
        SelectRecordsetObject q = Recover<SelectRecordsetObject>(Method(
            db2,
            "OpenRecordset",
            "SELECT ID, B / 2 AS hd, B \\ 2 AS idiv, B Mod 2 AS m, VarType(B) AS vt, TypeName(B) AS tn, IsNumeric(B) AS num FROM T WHERE ID IN (2, 3) ORDER BY ID",
            DbOpenSnapshot));
        SelectFieldsObject qf = Recover<SelectFieldsObject>(Get(q, "Fields"));
        Assert.Equal(16, IntOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "hd")), "Type")));
        Assert.Equal(-2L, LongOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "hd")), "Value")));
        Assert.Equal(-2L, LongOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "idiv")), "Value")));
        Assert.Equal(-1L, LongOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "m")), "Value")));
        Assert.Equal(20, IntOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "vt")), "Value")));
        Assert.Equal("LongLong", TextOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "tn")), "Value")));
        Assert.True(BoolOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "num")), "Value")));
        Assert.Null(Method(q, "MoveNext").Failure);
        Assert.Equal(2L, LongOf(Get(Recover<SelectFieldObject>(Get(qf, "Item", "hd")), "Value")));
        Assert.Null(Method(q, "Close").Failure);

        // CStr(Null) fails with 94 on Field.Value; CLng on 64-bit extreme fails 6 on Field.Value
        Assert.Equal(94, FirstCellFailure(db2, "SELECT CStr(B) FROM T WHERE ID = 5")?.Number);
        Assert.Equal(6, FirstCellFailure(db2, "SELECT CLng(B) FROM T WHERE ID = 4")?.Number);

        // Aggregates: standalone SUM (dbDouble), standalone MIN/MAX (dbBigInt), mixed SUM+MIN over extremes -> Group 5 Overflow (6)
        SelectRecordsetObject qSum = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT Sum(B) AS s FROM T WHERE ID IN (2, 3)", DbOpenSnapshot));
        SelectFieldObject fSum = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qSum, "Fields")), "Item", "s"));
        Assert.Equal(7, IntOf(Get(fSum, "Type")));
        Assert.Equal(0.0, DoubleOf(Get(fSum, "Value")));
        Assert.Null(Method(qSum, "Close").Failure);

        SelectRecordsetObject qMinMax = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT Min(B) AS mn, Max(B) AS mx FROM T", DbOpenSnapshot));
        SelectFieldsObject mmf = Recover<SelectFieldsObject>(Get(qMinMax, "Fields"));
        Assert.Equal(16, IntOf(Get(Recover<SelectFieldObject>(Get(mmf, "Item", "mn")), "Type")));
        Assert.Equal(long.MinValue, LongOf(Get(Recover<SelectFieldObject>(Get(mmf, "Item", "mn")), "Value")));
        Assert.Equal(long.MaxValue, LongOf(Get(Recover<SelectFieldObject>(Get(mmf, "Item", "mx")), "Value")));
        Assert.Null(Method(qMinMax, "Close").Failure);

        Assert.Equal(6, Method(db2, "OpenRecordset", "SELECT Sum(B), Min(B) FROM T", DbOpenSnapshot).Failure?.Number);

        // Unprobed intermediate magnitude in mixed SUM+MIN refuses by name
        Assert.Null(Method(db2, "Execute", "CREATE TABLE TMid (ID LONG CONSTRAINT PK_TM PRIMARY KEY, B BIGINT)", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO TMid VALUES (1, 5000000000)", DbFailOnError).Failure);
        Assert.StartsWith("NotModelled.BigIntMixedAggregateThreshold", Method(db2, "OpenRecordset", "SELECT Sum(B), Max(B) FROM TMid", DbOpenSnapshot).Failure?.Description);

        // Seek("=") on IX_B_DESC fails 3421
        RecordsetObject rsIdx = Recover<RecordsetObject>(Method(db2, "OpenRecordset", "T", DbOpenTable));
        Assert.Null(Put(rsIdx, "Index", "IX_B_DESC").Failure);
        Assert.Equal(3421, Method(rsIdx, "Seek", "=", 5L).Failure?.Number);
        Assert.Null(Method(rsIdx, "Close").Failure);

        // PARAMETERS p BigInt: Parameter.Type=19, exact VT_I8 binding, 3421 on overflow, NotModelled.BigIntParameterExecution on OpenRecordset
        QueryDefObject qd = Recover<QueryDefObject>(Method(db2, "CreateQueryDef", "", "PARAMETERS p BigInt; SELECT ID, B FROM T WHERE B = p"));
        DefinitionCollection ps = Recover<DefinitionCollection>(Get(qd, "Parameters"));
        ParameterObject p = Recover<ParameterObject>(Get(ps, "Item", "p"));
        Assert.Equal(19, IntOf(Get(p, "Type")));
        Assert.Null(Put(p, "Value", "9223372036854775807").Failure);
        Assert.Equal(long.MaxValue, LongOf(Get(p, "Value")));
        Assert.Equal(3421, Put(p, "Value", "9223372036854775808").Failure?.Number);
        Assert.StartsWith("NotModelled.BigIntParameterExecution", Method(qd, "OpenRecordset", DbOpenSnapshot).Failure?.Description);

        Assert.Null(Method(db2, "Close").Failure);
    }

    [Fact]
    public void DateTime2_dao_createfield_three_routes_indexes_sql_funcs_and_parameters()
    {
        string path = Path.Combine(_dir, "dt2.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(created, "Close").Failure);

        // DAO TableDef.CreateField("D", 26) upgrades page 0 format code to 0x06 ("32.0")
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        NewTableDefObject td = Recover<NewTableDefObject>(Method(db, "CreateTableDef", "T"));
        NewFieldsObject tfs = Recover<NewFieldsObject>(Get(td, "Fields"));
        NewFieldObject fId = Recover<NewFieldObject>(Method(td, "CreateField", "ID", 4));
        NewFieldObject fD = Recover<NewFieldObject>(Method(td, "CreateField", "D", 26));
        Assert.Equal(20, IntOf(Get(fD, "Type")));
        Assert.Equal(42, IntOf(Get(fD, "Size")));
        Assert.Equal(0, IntOf(Get(fD, "Attributes")));
        Assert.Null(Method(tfs, "Append", fId).Failure);
        Assert.Null(Method(tfs, "Append", fD).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        Assert.Null(Method(tds, "Append", td).Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Equal("32.0", TextOf(Get(db2, "Version")));
        DefinitionCollection tds2 = Recover<DefinitionCollection>(Get(db2, "TableDefs"));
        TableDefObject td2 = Recover<TableDefObject>(Get(tds2, "Item", "T"));
        DefinitionCollection fls2 = Recover<DefinitionCollection>(Get(td2, "Fields"));
        DefinitionFieldObject rD = Recover<DefinitionFieldObject>(Get(fls2, "Item", "D"));
        Assert.Equal(26, IntOf(Get(rD, "Type")));
        Assert.Equal(42, IntOf(Get(rD, "Size")));
        Assert.Equal(1, IntOf(Get(rD, "Attributes")));

        // UNIQUE index accepts multiple Nulls and rejects duplicate with 3022; Seek("=") fails 3421
        Assert.Null(Method(db2, "Execute", "CREATE UNIQUE INDEX UX_D ON T (D)", DbFailOnError).Failure);

        // Route 1: SQL INSERT (19d :7\0)
        Assert.Null(Method(db2, "Execute", "INSERT INTO T VALUES (1, '0001-01-01 00:00:00.0000000')", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO T VALUES (2, '2026-03-05 13:45:01.1234567')", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO T VALUES (3, '9999-12-31 23:59:59.9999999')", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO T VALUES (4, Null)", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO T VALUES (5, Null)", DbFailOnError).Failure);
        Assert.Equal(3022, Method(db2, "Execute", "INSERT INTO T VALUES (6, '2026-03-05 13:45:01.1234567')", DbFailOnError).Failure?.Number);
        Assert.Equal(3464, Method(db2, "Execute", "INSERT INTO T VALUES (6, '0000-01-01')", DbFailOnError).Failure?.Number);

        // Route 2: DAO Table AddNew (15d :7\0) + invalid put -> 0x800A000D without mutating DBEngine.Errors + Seek -> 3421
        RecordsetObject rs = Recover<RecordsetObject>(Method(db2, "OpenRecordset", "T", DbOpenTable));
        Assert.Null(Put(rs, "Index", "UX_D").Failure);
        Assert.Equal(3421, Method(rs, "Seek", "=", "2026-03-05 13:45:01.1234567").Failure?.Number);
        FieldsObject rsF = Recover<FieldsObject>(Get(rs, "Fields"));
        FieldObject fIdRs = Recover<FieldObject>(Get(rsF, "Item", "ID"));
        FieldObject fDRs = Recover<FieldObject>(Get(rsF, "Item", "D"));

        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(fIdRs, "Value", 6).Failure);
        Assert.Null(Put(fDRs, "Value", "1899-12-30 00:00:00.0000000").Failure);
        Assert.Null(Method(rs, "Update").Failure);

        // Invalid DAO put: first call surfaces stale 3421 EXCEPINFO, second call surfaces 0x800A000D
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(fIdRs, "Value", 7).Failure);
        Failure? inv1 = Put(fDRs, "Value", "bad").Failure;
        Assert.NotNull(inv1);
        Assert.Equal(3421, inv1!.Number);
        Failure? inv2 = Put(fDRs, "Value", "10000-01-01").Failure;
        Assert.NotNull(inv2);
        Assert.Equal(13, inv2!.Number);
        Assert.Null(Method(rs, "CancelUpdate").Failure);
        Assert.Null(Method(rs, "Close").Failure);

        // Route 3: SQL UPDATE truncates fractional seconds (:0\0)
        Assert.Null(Method(db2, "Execute", "UPDATE T SET D = '2026-03-05 13:45:01.7654321' WHERE ID = 2", DbFailOnError).Failure);
        SelectRecordsetObject qUpd = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT D FROM T WHERE ID = 2", DbOpenSnapshot));
        Assert.Equal("03/05/2026 13:45:01.0000000", TextOf(Get(Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qUpd, "Fields")), "Item", "D")), "Value")));
        Assert.Null(Method(qUpd, "Close").Failure);

        // SQL date functions across 0001..9999 vs 3071 on non-Null VBA funcs + Null propagation on Null rows
        SelectRecordsetObject qFuncs = Recover<SelectRecordsetObject>(Method(
            db2,
            "OpenRecordset",
            "SELECT ID, Year(D) AS y, DatePart('yyyy', D) AS dp, DateDiff('d', #2026-03-01#, D) AS dd FROM T WHERE ID IN (1, 2, 3, 4) ORDER BY ID",
            DbOpenSnapshot));
        SelectFieldsObject qff = Recover<SelectFieldsObject>(Get(qFuncs, "Fields"));
        Assert.Equal(1, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "y")), "Value")));
        Assert.Equal(1, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "dp")), "Value")));
        Assert.Equal(-739675, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "dd")), "Value")));
        Assert.Null(Method(qFuncs, "MoveNext").Failure);
        Assert.Equal(2026, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "y")), "Value")));
        Assert.Equal(4, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "dd")), "Value")));
        Assert.Null(Method(qFuncs, "MoveNext").Failure);
        Assert.Equal(9999, IntOf(Get(Recover<SelectFieldObject>(Get(qff, "Item", "y")), "Value")));
        Assert.Null(Method(qFuncs, "MoveNext").Failure);
        Assert.True(IsNullResult(Get(Recover<SelectFieldObject>(Get(qff, "Item", "y")), "Value")));
        Assert.Null(Method(qFuncs, "Close").Failure);

        // DateAdd fails 3464 on year < 100 and 3071 on overflow past 9999-12-31 (at Field.Value)
        Assert.Equal(3464, FirstCellFailure(db2, "SELECT DateAdd('d', 1, D) FROM T WHERE ID = 1")?.Number);
        Assert.Equal(3071, FirstCellFailure(db2, "SELECT DateAdd('d', 1, D) FROM T WHERE ID = 3")?.Number);

        // VarType/TypeName/IsDate/IsNull/CStr/CDate fail 3071 on non-Null (at Field.Value) and succeed on Null
        Assert.Equal(3071, FirstCellFailure(db2, "SELECT VarType(D) FROM T WHERE ID = 2")?.Number);
        Assert.Equal(3071, FirstCellFailure(db2, "SELECT TypeName(D) FROM T WHERE ID = 2")?.Number);
        Assert.Equal(3071, FirstCellFailure(db2, "SELECT CStr(D) FROM T WHERE ID = 2")?.Number);
        Assert.Equal(3071, FirstCellFailure(db2, "SELECT IsNull(D) FROM T WHERE ID = 2")?.Number);

        SelectRecordsetObject qNullFuncs = Recover<SelectRecordsetObject>(Method(
            db2,
            "OpenRecordset",
            "SELECT VarType(D) AS vt, TypeName(D) AS tn, IsDate(D) AS isd, IsNull(D) AS isn, (D IS NULL) AS sqln FROM T WHERE ID = 4",
            DbOpenSnapshot));
        SelectFieldsObject qnf = Recover<SelectFieldsObject>(Get(qNullFuncs, "Fields"));
        Assert.Equal(1, IntOf(Get(Recover<SelectFieldObject>(Get(qnf, "Item", "vt")), "Value")));
        Assert.Equal("Null", TextOf(Get(Recover<SelectFieldObject>(Get(qnf, "Item", "tn")), "Value")));
        Assert.False(BoolOf(Get(Recover<SelectFieldObject>(Get(qnf, "Item", "isd")), "Value")));
        Assert.True(BoolOf(Get(Recover<SelectFieldObject>(Get(qnf, "Item", "isn")), "Value")));
        Assert.True(BoolOf(Get(Recover<SelectFieldObject>(Get(qnf, "Item", "sqln")), "Value")));
        Assert.Null(Method(qNullFuncs, "Close").Failure);

        // Aggregates: MIN/MAX succeed (Type=26, Size=42), SUM/AVG fail 3464
        Assert.Equal(3464, Method(db2, "OpenRecordset", "SELECT Sum(D) FROM T", DbOpenSnapshot).Failure?.Number);
        Assert.Equal(3464, Method(db2, "OpenRecordset", "SELECT Avg(D) FROM T", DbOpenSnapshot).Failure?.Number);
        SelectRecordsetObject qMm = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT Min(D) AS mn, Max(D) AS mx, Count(D) AS c FROM T", DbOpenSnapshot));
        SelectFieldsObject qmmf = Recover<SelectFieldsObject>(Get(qMm, "Fields"));
        Assert.Equal(26, IntOf(Get(Recover<SelectFieldObject>(Get(qmmf, "Item", "mn")), "Type")));
        Assert.Equal(42, IntOf(Get(Recover<SelectFieldObject>(Get(qmmf, "Item", "mn")), "Size")));
        Assert.Equal("01/01/0001 00:00:00.0000000", TextOf(Get(Recover<SelectFieldObject>(Get(qmmf, "Item", "mn")), "Value")));
        Assert.Equal(4, IntOf(Get(Recover<SelectFieldObject>(Get(qmmf, "Item", "c")), "Value")));
        Assert.Null(Method(qMm, "Close").Failure);

        // Two-column DATETIME2 table: D = D2 succeeds on non-Null rows, fails 3464 when either side is Null
        Assert.Null(Method(db2, "Execute", "CREATE TABLE TCmp (ID LONG CONSTRAINT PK_TC PRIMARY KEY, D DATETIME2, D2 DATETIME2)", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO TCmp VALUES (1, '2026-03-05 00:00:00', '2026-03-05 00:00:00')", DbFailOnError).Failure);
        Assert.Null(Method(db2, "Execute", "INSERT INTO TCmp VALUES (2, '2026-03-05 00:00:00', Null)", DbFailOnError).Failure);
        SelectRecordsetObject qCmpOk = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT ID FROM TCmp WHERE ID = 1 AND D = D2", DbOpenSnapshot));
        Assert.Equal(1, IntOf(Get(Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qCmpOk, "Fields")), "Item", "ID")), "Value")));
        Assert.Null(Method(qCmpOk, "Close").Failure);
        Failure? fCmp = Method(db2, "OpenRecordset", "SELECT ID FROM TCmp WHERE D = D2 ORDER BY ID", DbOpenSnapshot).Failure;
        Assert.True(fCmp?.Number == 3464, $"Expected 3464, got {fCmp?.Number}: {fCmp?.Description}");

        // PARAMETERS p DateTime2 (Type=20, BSTR->3421, VT_DATE->VT_DECIMAL->3061, Null->3464) & DateTimeExtended -> 3139
        Assert.Equal(3139, Method(db2, "CreateQueryDef", "QDtExt", "PARAMETERS p DateTimeExtended; SELECT ID FROM T WHERE D = p").Failure?.Number);
        QueryDefObject qd = Recover<QueryDefObject>(Method(db2, "CreateQueryDef", "", "PARAMETERS p DateTime2; SELECT ID FROM T WHERE D = p ORDER BY ID"));
        DefinitionCollection ps = Recover<DefinitionCollection>(Get(qd, "Parameters"));
        ParameterObject p = Recover<ParameterObject>(Get(ps, "Item", "p"));
        Assert.Equal(20, IntOf(Get(p, "Type")));
        Assert.Equal(3421, Put(p, "Value", "2026-03-05").Failure?.Number);
        Assert.Null(Put(p, "Value", new DateTime(2026, 3, 5)).Failure);
        NativeVariant coerced = ReadVariant(Get(p, "Value"));
        Assert.Equal((ushort)VarType.Decimal, coerced.Vt);
        Assert.Equal(3061, Method(qd, "OpenRecordset", DbOpenSnapshot).Failure?.Number);
        Assert.Null(Put(p, "Value", DBNull.Value).Failure);
        Assert.Equal(3464, Method(qd, "OpenRecordset", DbOpenSnapshot).Failure?.Number);

        // Dynaset Edit truncates fractional seconds just like SQL UPDATE
        SelectRecordsetObject dyn = Recover<SelectRecordsetObject>(Method(db2, "OpenRecordset", "SELECT ID, D FROM T WHERE ID = 1", DbOpenDynaset));
        Assert.Null(Method(dyn, "Edit").Failure);
        SelectFieldObject dynD = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(dyn, "Fields")), "Item", "D"));
        Assert.Null(Put(dynD, "Value", "2020-02-29 10:20:30.9876543").Failure);
        Assert.Null(Method(dyn, "Update").Failure);
        Assert.Equal("02/29/2020 10:20:30.0000000", TextOf(Get(dynD, "Value")));
        Assert.Null(Method(dyn, "Close").Failure);

        Assert.Null(Method(db2, "Close").Failure);
    }

    [Fact]
    public void Gate4_regression_excep_null_strings_bigint_div_mixed_agg_r8_overflow_and_dt2_actions()
    {
        string path = Path.Combine(_dir, "gate4-reg.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));

        // 1. Invalid DATETIME2 put with empty DBEngine.Errors emits null EXCEPINFO source/description pointers
        // (evidence/r-q49-dt2-draftprops/dt2-update-invalid-accdb calls 196/216/236/256/276).
        Assert.Null(Method(db, "Execute", "CREATE TABLE TD (ID LONG CONSTRAINT PK_TD PRIMARY KEY, D DATETIME2, D2 DATETIME2)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TD VALUES (1, '2026-03-05 00:00:00', '2026-03-06 00:00:00')", DbFailOnError).Failure);

        RecordsetObject rsDt = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TD", DbOpenTable));
        Assert.Null(Method(rsDt, "AddNew").Failure);
        FieldObject fDt = Recover<FieldObject>(Get(Recover<FieldsObject>(Get(rsDt, "Fields")), "Item", "D"));
        nint badBstr = Marshal.StringToBSTR("not-a-date");
        try
        {
            var badVal = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = badBstr };
            int propPutId = -3;
            var dp = new DispParams { Args = (nint)(&badVal), ArgCount = 1, NamedArgs = (nint)(&propPutId), NamedArgCount = 1 };
            NativeVariant res = default;
            ExcepInfo ex = default;
            uint argErr = 0;
            int hr = fDt.Invoke(fDt.Declaration.Member("Value", InvokeKind.PropertyPut).MemberId, (ushort)InvokeKind.PropertyPut, &dp, &res, &ex, &argErr);
            Assert.Equal(HResults.DISP_E_EXCEPTION, hr);
            Assert.Equal(unchecked((int)0x800A000D), ex.Scode);
            Assert.Equal(0, ex.Source);
            Assert.Equal(0, ex.Description);
            Assert.Equal(0, ex.HelpFile);
            Assert.Equal(0u, ex.HelpContext);
            Assert.False(NativeRoute.GetErrorInfo(0, out nint ei) == 0 && ei != 0);
        }
        finally
        {
            Marshal.FreeBSTR(badBstr);
        }
        Assert.Null(Method(rsDt, "CancelUpdate").Failure);
        Assert.Null(Method(rsDt, "Close").Failure);

        // 2. DATETIME2 column-to-column UPDATE refuses via NotModelled.ActionConversion (never 3464).
        Failure? dtUpdFail = Method(db, "Execute", "UPDATE TD SET D = D2 WHERE ID = 1", DbFailOnError).Failure;
        Assert.Equal(FailureKind.NotModelled, dtUpdFail?.Kind);
        Assert.Contains("NotModelled.ActionConversion", dtUpdFail!.Description);

        // 3. Action QueryDef with DateTime2 parameter (DaoType 20): unset or VT_DATE-coerced-to-VT_DECIMAL
        // fails 3061 with total declared parameter count "Too few parameters. Expected 2."
        // (evidence/r-q49-dt2-draftprops/dt2-params-dao-querydef-accdb call 292).
        QueryDefObject qIns = Recover<QueryDefObject>(Method(db, "CreateQueryDef", "", "PARAMETERS pid Long, pv DateTime2; INSERT INTO TD (ID, D) VALUES (pid, pv);"));
        DefinitionCollection qInsPs = Recover<DefinitionCollection>(Get(qIns, "Parameters"));
        ParameterObject pId = Recover<ParameterObject>(Get(qInsPs, "Item", "pid"));
        ParameterObject pV = Recover<ParameterObject>(Get(qInsPs, "Item", "pv"));
        Assert.Null(Put(pId, "Value", 99).Failure);
        Failure? fUnset = Method(qIns, "Execute", DbFailOnError).Failure;
        Assert.Equal(3061, fUnset?.Number);
        Assert.Equal("Too few parameters. Expected 2.", fUnset?.Description);
        Assert.Null(Put(pV, "Value", new DateTime(2026, 3, 5)).Failure);
        Failure? fDec = Method(qIns, "Execute", DbFailOnError).Failure;
        Assert.Equal(3061, fDec?.Number);
        Assert.Equal("Too few parameters. Expected 2.", fDec?.Description);

        // 4. BIGINT B / 2 metadata & oleaut32 VarDiv semantics (r-def-expr-sql/expr-bigint-stored-arith #364/#369/#554/#784)
        // + strict operand pairs in SqlBinder + r8OverflowAs6 scoping on UPDATE vs INSERT...SELECT.
        Assert.Null(Method(db, "Execute", "CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, G LONG, B BIGINT)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (1, 1, 9223372036854775807)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (2, 1, -9223372036854775808)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (3, 2, 4611686018427387904)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (4, 3, 0)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (5, 3, 2147483648)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (6, 4, -1)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TB VALUES (7, 5, 9223372036854775807)", DbFailOnError).Failure);

        // #364/#369: long.MaxValue / 2 -> Field.Type 16, Field.Value fails 6 (Overflow), VarType(B / 2) == 5 (VT_R8)
        SelectRecordsetObject qDivMax = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", "SELECT B / 2 AS v, VarType(B / 2) AS vt FROM TB WHERE ID = 1", DbOpenSnapshot));
        SelectFieldsObject qDivMaxF = Recover<SelectFieldsObject>(Get(qDivMax, "Fields"));
        SelectFieldObject fDivV = Recover<SelectFieldObject>(Get(qDivMaxF, "Item", "v"));
        Assert.Equal(16, IntOf(Get(fDivV, "Type")));
        Assert.Equal(6, Get(fDivV, "Value").Failure?.Number);
        Assert.Equal(5, IntOf(Get(Recover<SelectFieldObject>(Get(qDivMaxF, "Item", "vt")), "Value")));
        Assert.Null(Method(qDivMax, "Close").Failure);

        // #554/#784: long.MinValue / 2 and 2^62 / 2 return exact VT_I8 with VarType == 5
        SelectRecordsetObject qDivExact = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", "SELECT ID, B / 2 AS v, VarType(B / 2) AS vt FROM TB WHERE ID IN (2, 3) ORDER BY ID", DbOpenSnapshot));
        SelectFieldsObject qDivExactF = Recover<SelectFieldsObject>(Get(qDivExact, "Fields"));
        Assert.Equal(-4611686018427387904L, LongOf(Get(Recover<SelectFieldObject>(Get(qDivExactF, "Item", "v")), "Value")));
        Assert.Equal(5, IntOf(Get(Recover<SelectFieldObject>(Get(qDivExactF, "Item", "vt")), "Value")));
        Assert.Null(Method(qDivExact, "MoveNext").Failure);
        Assert.Equal(2305843009213693952L, LongOf(Get(Recover<SelectFieldObject>(Get(qDivExactF, "Item", "v")), "Value")));
        Assert.Equal(5, IntOf(Get(Recover<SelectFieldObject>(Get(qDivExactF, "Item", "vt")), "Value")));
        Assert.Null(Method(qDivExact, "Close").Failure);

        // Unprobed BIGINT operand pairs leave static Field.Type Unknown -> NotModelled (no generalization beyond recorded pairs)
        foreach (string unprobedSql in new[] { "SELECT 1 + B AS v FROM TB WHERE ID = 4", "SELECT B / B AS v FROM TB WHERE ID = 3" })
        {
            SelectRecordsetObject qUnprobed = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", unprobedSql, DbOpenSnapshot));
            SelectFieldObject fUnprobed = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qUnprobed, "Fields")), "Item", "v"));
            Assert.Equal(FailureKind.NotModelled, Get(fUnprobed, "Type").Failure?.Kind);
            Assert.Null(Method(qUnprobed, "Close").Failure);
        }

        // 5. Group 5 mixed SUM+MIN/MAX over BIGINT (r-q48-ado-batch-edits/bi-agg-g5-isolate-accdb):
        // G=3 {0, 2^31} succeeds; G=4 {-1} and isolated G=5 {MaxValue} refuse NotModelled.BigIntMixedAggregateThreshold;
        // G=1 {MaxValue first, MinValue second} still fails 6 regardless of scan order!
        SelectRecordsetObject qG3 = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", "SELECT SUM(B) AS s, MIN(B) AS mn FROM TB WHERE G = 3", DbOpenSnapshot));
        Assert.Equal(2147483648.0, DoubleOf(Get(Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qG3, "Fields")), "Item", "s")), "Value")));
        Assert.Null(Method(qG3, "Close").Failure);

        Failure? fNeg = Method(db, "OpenRecordset", "SELECT SUM(B) AS s, MIN(B) AS mn FROM TB WHERE G = 4", DbOpenSnapshot).Failure;
        Assert.Equal(FailureKind.NotModelled, fNeg?.Kind);
        Assert.Contains("NotModelled.BigIntMixedAggregateThreshold", fNeg!.Description);

        Failure? fMaxOnly = Method(db, "OpenRecordset", "SELECT SUM(B) AS s, MIN(B) AS mn FROM TB WHERE G = 5", DbOpenSnapshot).Failure;
        Assert.Equal(FailureKind.NotModelled, fMaxOnly?.Kind);
        Assert.Contains("NotModelled.BigIntMixedAggregateThreshold", fMaxOnly!.Description);

        Failure? fReversed = Method(db, "OpenRecordset", "SELECT SUM(B) AS s, MIN(B) AS mn FROM TB WHERE G = 1", DbOpenSnapshot).Failure;
        Assert.Equal(6, fReversed?.Number);

        // 6. r8OverflowAs6 scoping: literal VT_R8 1E19 fails 3464 in UPDATE and INSERT...SELECT,
        // while BIGINT expression overflow (B + 1 on long.MaxValue) fails 6 in both.
        Assert.Equal(3464, Method(db, "Execute", "UPDATE TB SET B = 1E19 WHERE ID = 4", DbFailOnError).Failure?.Number);
        Assert.Equal(3464, Method(db, "Execute", "INSERT INTO TB (ID, G, B) SELECT 99, 9, 1E19 FROM TB WHERE ID = 4", DbFailOnError).Failure?.Number);
        Assert.Equal(6, Method(db, "Execute", "UPDATE TB SET B = B + 1 WHERE ID = 1", DbFailOnError).Failure?.Number);
        Assert.Equal(6, Method(db, "Execute", "INSERT INTO TB (ID, G, B) SELECT 99, 9, B + 1 FROM TB WHERE ID = 1", DbFailOnError).Failure?.Number);

        Assert.Null(Method(db, "Close").Failure);
    }
}
