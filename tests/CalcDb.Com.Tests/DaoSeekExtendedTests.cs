using System.Runtime.InteropServices;

using AdoConnectionObject = CalcDb.Com.Ado.ConnectionObject;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Format;
using CalcDb.Foundation;
using CalcDb.Isam;
using CalcDb.Isam.Edit;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// End-to-end COM DAO tests for <c>jqv9</c> descending <c>DECIMAL</c>/<c>BINARY</c> indexes and
/// <c>Recordset.Seek("=")</c> argument matrices over <c>GUID</c>, <c>BINARY(16)</c>/<c>VARBINARY(16)</c>,
/// and <c>DECIMAL(18,4)</c> on ACCDB &amp; Jet 4 MDB (<c>evidence/r-q51-codecs-props-compact/{dec-desc-keys,bin-desc-keys,seek-args-*}-*</c>).
/// </summary>
public sealed unsafe class DaoSeekExtendedTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbOpenTable = 1;
    private const int DbOpenSnapshot = 4;

    private static readonly int[] NullRowIds = [1, 2];
    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-seek-ext-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(0xDA0_5EE9, Standalone: true, 0, 0, 0, 0, 0));
    private readonly HostBinding _adoBinding = new(0xAD0_5EE9, Standalone: true, 0, 0, 0, 0, 0);

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
            else if (v->Vt == (ushort)(VarType.Array | VarType.UI1) && v->Pointer != 0)
            {
                using OwnedVariant cleanup = OwnedVariant.TakeArray(v->Pointer, VarType.UI1);
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

    private AdoConnectionObject CreateConnection()
    {
        nint conn;
        Assert.Equal(0, AdoConnectionObject.Create(_adoBinding, 0, NativeRoute.IidIDispatch, &conn));
        _held.Add(conn);
        return ComObject.Recover<AdoConnectionObject>(conn);
    }

    private nint Arg(object? value)
    {
        if (value is null)
        {
            return 0;
        }
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case int i: v->Vt = (ushort)VarType.I4; v->Int64 = (uint)i; break;
            case double d: v->Vt = (ushort)VarType.R8; v->Double = d; break;
            case bool b: v->Vt = (ushort)VarType.Bool; v->Int64 = b ? 0xFFFF : 0; break;
            case string text: v->Vt = (ushort)VarType.Bstr; v->Pointer = Marshal.StringToBSTR(text); break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.AsSpan().CopyTo(new Span<byte>((void*)SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                v->Vt = (ushort)(VarType.Array | VarType.UI1);
                v->Pointer = psa;
                break;
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

    private static int IntOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        int v = (int)(result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64);
        result.ReleaseUntaken();
        return v;
    }

    private static bool BoolOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        bool v = (result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64) != 0;
        result.ReleaseUntaken();
        return v;
    }

    [Theory]
    [InlineData("bin-seek.accdb", DbVersion120)]
    [InlineData("bin-seek.mdb", DbVersion40)]
    public void Binary_desc_index_order_by_and_seek_matrix_on_accdb_and_mdb(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(created, "Execute", "CREATE TABLE TB (ID LONG CONSTRAINT PK_TB PRIMARY KEY, B VARBINARY(16))", DbFailOnError).Failure);
        Assert.Null(Method(created, "Close").Failure);

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        RecordsetObject rs = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TB", DbOpenTable));
        FieldsObject fs = Recover<FieldsObject>(Get(rs, "Fields"));
        FieldObject fId = Recover<FieldObject>(Get(fs, "Item", "ID"));
        FieldObject fB = Recover<FieldObject>(Get(fs, "Item", "B"));

        void Add(int id, object? b)
        {
            Assert.Null(Method(rs, "AddNew").Failure);
            Assert.Null(Put(fId, "Value", id).Failure);
            Assert.Null(Put(fB, "Value", b).Failure);
            Assert.Null(Method(rs, "Update").Failure);
        }

        Add(1, DBNull.Value);
        Add(2, Array.Empty<byte>()); // empty byte[] stores Null (r-q51/bin-desc-keys)
        Add(3, new byte[] { 0x41, 0x42, 0x43 });
        Add(4, new byte[] { 0x41, 0x42, 0x43, 0x00 });
        Add(5, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09 });
        Add(6, new byte[] { 0x41, 0x00, 0x42, 0x00 }); // UTF-16LE "AB"
        Assert.Null(Method(rs, "Close").Failure);

        Assert.Null(Method(db, "Execute", "CREATE INDEX IX_B_DESC ON TB (B DESC)", DbFailOnError).Failure);

        // ORDER BY B DESC puts non-Null descending, Nulls last
        SelectRecordsetObject q = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", "SELECT ID FROM TB ORDER BY B DESC, ID", DbOpenSnapshot));
        SelectFieldObject qId = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(q, "Fields")), "Item", "ID"));
        var ids = new List<int>();
        while (!BoolOf(Get(q, "EOF")))
        {
            ids.Add(IntOf(Get(qId, "Value")));
            Assert.Null(Method(q, "MoveNext").Failure);
        }
        Assert.Equal([4, 3, 6, 5, 1, 2], ids);
        Assert.Null(Method(q, "Close").Failure);

        // Seek matrix on IX_B_DESC: odd-length byte[] truncated to len & ~1, BSTR -> UTF-16LE, empty -> Null
        RecordsetObject rsSeek = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TB", DbOpenTable));
        Assert.Null(Put(rsSeek, "Index", "IX_B_DESC").Failure);
        FieldObject rsId = Recover<FieldObject>(Get(Recover<FieldsObject>(Get(rsSeek, "Fields")), "Item", "ID"));

        // Exact 4-byte match
        Assert.Null(Method(rsSeek, "Seek", "=", new byte[] { 0x41, 0x42, 0x43, 0x00 }).Failure);
        Assert.False(BoolOf(Get(rsSeek, "NoMatch")));
        Assert.Equal(4, IntOf(Get(rsId, "Value")));

        // Odd 3-byte arg truncates to 2 bytes (0x41, 0x42) -> NoMatch (does NOT match 3-byte row 3!)
        Assert.Null(Method(rsSeek, "Seek", "=", new byte[] { 0x41, 0x42, 0x43 }).Failure);
        Assert.True(BoolOf(Get(rsSeek, "NoMatch")));

        // BSTR "AB" -> UTF-16LE 41 00 42 00 -> matches row 6
        Assert.Null(Method(rsSeek, "Seek", "=", "AB").Failure);
        Assert.False(BoolOf(Get(rsSeek, "NoMatch")));
        Assert.Equal(6, IntOf(Get(rsId, "Value")));

        // Empty byte[] or "" -> Null -> matches Null row (ID 1 or 2)
        Assert.Null(Method(rsSeek, "Seek", "=", "").Failure);
        Assert.False(BoolOf(Get(rsSeek, "NoMatch")));
        Assert.Contains(IntOf(Get(rsId, "Value")), NullRowIds);

        Assert.Null(Method(rsSeek, "Close").Failure);
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData("dec-guid-seek.accdb", DbVersion120)]
    [InlineData("dec-guid-seek.mdb", DbVersion40)]
    public void Decimal_and_Guid_seek_matrices_match_r_q51(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(created, "Execute", "CREATE TABLE TG (ID LONG CONSTRAINT PK_TG PRIMARY KEY, G GUID)", DbFailOnError).Failure);
        Assert.Null(Method(created, "Close").Failure);

        // Create TD(ID PK, D DECIMAL(18, 4)) via ADO DDL (matching evidence/r-q51-codecs-props-compact/dec-desc-keys-*)
        AdoConnectionObject conn = CreateConnection();
        Assert.Null(Method(conn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path}", null, null, null).Failure);
        Assert.Null(Method(conn, "Execute", "CREATE TABLE TD (ID LONG CONSTRAINT PK_TD PRIMARY KEY, D DECIMAL(18, 4))", null, null).Failure);
        Assert.Null(Method(conn, "Close").Failure);

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));

        // Insert rows into TD (including Null and negative/positive numbers) and create DESC index
        Assert.Null(Method(db, "Execute", "INSERT INTO TD VALUES (1, Null)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TD VALUES (2, -100.25)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TD VALUES (3, 0)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO TD VALUES (4, 123.45)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE INDEX IX_D_DESC ON TD (D DESC)", DbFailOnError).Failure);

        // ORDER BY D DESC -> 4, 3, 2, 1
        SelectRecordsetObject qDec = Recover<SelectRecordsetObject>(Method(db, "OpenRecordset", "SELECT ID FROM TD ORDER BY D DESC", DbOpenSnapshot));
        SelectFieldObject qDecId = Recover<SelectFieldObject>(Get(Recover<SelectFieldsObject>(Get(qDec, "Fields")), "Item", "ID"));
        var decIds = new List<int>();
        while (!BoolOf(Get(qDec, "EOF")))
        {
            decIds.Add(IntOf(Get(qDecId, "Value")));
            Assert.Null(Method(qDec, "MoveNext").Failure);
        }
        Assert.Equal([4, 3, 2, 1], decIds);
        Assert.Null(Method(qDec, "Close").Failure);

        // DECIMAL Seek("=") coerces EVERY argument (int, double, string, bad text, Null) to Null with zero 3421s!
        RecordsetObject rsDec = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TD", DbOpenTable));
        Assert.Null(Put(rsDec, "Index", "IX_D_DESC").Failure);
        FieldObject decRsId = Recover<FieldObject>(Get(Recover<FieldsObject>(Get(rsDec, "Fields")), "Item", "ID"));
        foreach (object? arg in new object?[] { 100, -100, 0.0, "100", "not-a-number", DBNull.Value })
        {
            Assert.Null(Method(rsDec, "Seek", "=", arg).Failure);
            Assert.False(BoolOf(Get(rsDec, "NoMatch")));
            Assert.Equal(1, IntOf(Get(decRsId, "Value")));
        }
        Assert.Null(Method(rsDec, "Close").Failure);

        // GUID Seek("="): {guid {...}} and {...} match; 16-byte array, unbraced string, and I4 fail 3421; Null matches Null
        Guid g1 = Guid.Parse("00112233-4455-6677-8899-AABBCCDDEEFF");
        RecordsetObject rsGAdd = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TG", DbOpenTable));
        FieldsObject gAddFields = Recover<FieldsObject>(Get(rsGAdd, "Fields"));
        FieldObject gAddId = Recover<FieldObject>(Get(gAddFields, "Item", "ID"));
        FieldObject gAddVal = Recover<FieldObject>(Get(gAddFields, "Item", "G"));
        Assert.Null(Method(rsGAdd, "AddNew").Failure);
        Assert.Null(Put(gAddId, "Value", 1).Failure);
        Assert.Null(Put(gAddVal, "Value", DBNull.Value).Failure);
        Assert.Null(Method(rsGAdd, "Update").Failure);
        Assert.Null(Method(rsGAdd, "AddNew").Failure);
        Assert.Null(Put(gAddId, "Value", 2).Failure);
        Assert.Null(Put(gAddVal, "Value", "{00112233-4455-6677-8899-AABBCCDDEEFF}").Failure);
        Assert.Null(Method(rsGAdd, "Update").Failure);
        Assert.Null(Method(rsGAdd, "Close").Failure);
        Assert.Null(Method(db, "Execute", "CREATE INDEX IX_G ON TG (G)", DbFailOnError).Failure);

        RecordsetObject rsG = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TG", DbOpenTable));
        Assert.Null(Put(rsG, "Index", "IX_G").Failure);
        FieldObject gRsId = Recover<FieldObject>(Get(Recover<FieldsObject>(Get(rsG, "Fields")), "Item", "ID"));

        Assert.Null(Method(rsG, "Seek", "=", "{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}").Failure);
        Assert.False(BoolOf(Get(rsG, "NoMatch")));
        Assert.Equal(2, IntOf(Get(gRsId, "Value")));

        Assert.Null(Method(rsG, "Seek", "=", "{00112233-4455-6677-8899-AABBCCDDEEFF}").Failure);
        Assert.False(BoolOf(Get(rsG, "NoMatch")));
        Assert.Equal(2, IntOf(Get(gRsId, "Value")));

        Assert.Equal(3421, Method(rsG, "Seek", "=", g1.ToByteArray()).Failure?.Number);
        Assert.Equal(3421, Method(rsG, "Seek", "=", "00112233-4455-6677-8899-AABBCCDDEEFF").Failure?.Number);
        Assert.Equal(3421, Method(rsG, "Seek", "=", 42).Failure?.Number);

        Assert.Null(Method(rsG, "Seek", "=", DBNull.Value).Failure);
        Assert.False(BoolOf(Get(rsG, "NoMatch")));
        Assert.Equal(1, IntOf(Get(gRsId, "Value")));

        Assert.Null(Method(rsG, "Close").Failure);
        Assert.Null(Method(db, "Close").Failure);
    }

    [Fact]
    public void Gate4_regression_decimal_scale_overflow_guard_binary_empty_bstr_gate_and_bare_ado_alter_decimal()
    {
        // 1. AssignmentConversion.Numeric guards UInt128 magnitude scaling against wrap before multiplication.
        var dec28_28 = new ColumnDescriptor
        {
            Name = "D", TypeCode = NativeTypes.Numeric, PhysicalNumber = 1, VariableIndex = 0, ColumnId = 1,
            TypeInfo = 0, Flags = 0x01, ExtendedFlags = 0, FixedOffset = 0, Length = 17, IsFixed = true,
            NumericPrecision = 28, NumericScale = 28,
        };
        var dec18_4 = dec28_28 with { NumericPrecision = 18, NumericScale = 4 };
        using OwnedVariant decMax = OwnedVariant.FromDecimal(DecimalValue.FromDecimal(decimal.MaxValue));
        Assert.Equal(MutationOpen.ConversionNotRecorded, AssignmentConversion.Convert(*decMax.Pointer, dec28_28, 1033).Open);
        Assert.Equal(MutationOpen.ConversionNotRecorded, AssignmentConversion.Convert(*decMax.Pointer, dec18_4, 1033).Open);

        // 2. Empty BSTR -> Null on BINARY/VARBINARY is gated to DAO table puts (allowEmptyBstrBinaryNull: true);
        // default (ADO immediate edit) keeps MutationOpen.ColumnType.
        var binCol = new ColumnDescriptor
        {
            Name = "B", TypeCode = NativeTypes.Binary, PhysicalNumber = 1, VariableIndex = 0, ColumnId = 1,
            TypeInfo = 0, Flags = 0x00, ExtendedFlags = 0, FixedOffset = 0, Length = 16, IsFixed = false,
        };
        nint emptyBstr = Marshal.StringToBSTR("");
        try
        {
            var emptyVar = new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = emptyBstr };
            Assert.Equal(MutationOpen.ColumnType, AssignmentConversion.Convert(emptyVar, binCol, 1033).Open);
            ConversionResult daoOk = AssignmentConversion.Convert(emptyVar, binCol, 1033, allowEmptyBstrBinaryNull: true);
            Assert.True(daoOk.Succeeded);
            Assert.Equal(NativeKind.Null, daoOk.Value.Kind);
        }
        finally
        {
            Marshal.FreeBSTR(emptyBstr);
        }

        // 3. Bare ADO ALTER TABLE ADD COLUMN N DECIMAL reaches DdlBinder's NotModelled.DdlAlterTable
        // (not NotModelled.DdlAlterTypeSyntax).
        string path = Path.Combine(_dir, "bare-alter-dec.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(created, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY)", DbFailOnError).Failure);
        Assert.Null(Method(created, "Close").Failure);

        AdoConnectionObject conn = CreateConnection();
        Assert.Null(Method(conn, "Open", $"Provider=Microsoft.ACE.OLEDB.12.0;Data Source={path}", null, null, null).Failure);
        Failure? alterFail = Method(conn, "Execute", "ALTER TABLE T ADD COLUMN N DECIMAL", null, null).Failure;
        Assert.Equal(FailureKind.NotModelled, alterFail?.Kind);
        Assert.Contains("NotModelled.DdlAlterTable", alterFail!.Description);
        Assert.Null(Method(conn, "Close").Failure);
    }
}
