using System.Runtime.InteropServices;

using AdoConnectionObject = CalcDb.Com.Ado.ConnectionObject;
using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// End-to-end COM DAO tests for:
/// <list type="bullet">
/// <item><c>evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb</c> &amp;
/// <c>evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-mdb</c>: DAO dynaset <c>Field.Value</c> scalar conversions,
/// half-to-even rounding, <c>VT_EMPTY</c>, <c>Null</c> into Yes/No, <c>3421</c>/<c>3759</c>/<c>3761</c>, and
/// <c>Missing</c> (<c>0x80020004</c>).</item>
/// <item><c>evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb</c> &amp;
/// <c>evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-mdb</c> (calls 65[17], 70[17], 75[17], 75[19], 80[17],
/// 97[17]): <c>Field.Properties("AppendOnly")</c> and <c>Field.Properties("FieldSize")</c> on dynaset/snapshot.</item>
/// </list>
/// </summary>
public sealed unsafe class DaoConvAndExtentTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbOpenDynaset = 2;
    private const int DbOpenSnapshot = 4;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-conv-ext-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C011, Standalone: true, 0, 0, 0, 0, 0));
    private readonly HostBinding _adoBinding = new(0xAD0_C011, Standalone: true, 0, 0, 0, 0, 0);

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

    private readonly record struct EmptyArg;
    private readonly record struct MissingArg;
    private readonly record struct CurrencyArg(long Raw);

    private nint Arg(object? value)
    {
        if (value is null)
        {
            return 0;
        }
        var v = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        switch (value)
        {
            case EmptyArg:
                v->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                v->Vt = (ushort)VarType.Null;
                break;
            case MissingArg:
                v->Vt = (ushort)VarType.Error;
                v->Int64 = unchecked((int)0x80020004);
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
            case CurrencyArg cy:
                v->Vt = (ushort)VarType.Currency;
                v->Int64 = cy.Raw;
                break;
            case string text:
                v->Vt = (ushort)VarType.Bstr;
                v->Pointer = Marshal.StringToBSTR(text);
                break;
            case byte[] bytes:
                nint psa = SafeArrays.Create(VarType.UI1, (0, bytes.Length));
                if (bytes.Length > 0)
                {
                    bytes.AsSpan().CopyTo(new Span<byte>((void*)SafeArrays.ElementPointer(psa, 0), bytes.Length));
                }
                v->Vt = (ushort)(VarType.Array | VarType.UI1);
                v->Pointer = psa;
                break;
            default:
                throw new NotSupportedException(value.GetType().FullName);
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

    private static long LongOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        long v = result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64;
        result.ReleaseUntaken();
        return v;
    }

    private static bool BoolOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        bool v = (short)(result.HasScalar ? result.Scalar.Int64 : result.Value!.Pointer->Int64) != 0;
        result.ReleaseUntaken();
        return v;
    }

    private static VarType TypeOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        VarType vt = result.HasScalar ? result.Scalar.Type : result.Value!.Pointer->Type;
        result.ReleaseUntaken();
        return vt;
    }

    private static string StrOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint bstr = result.HasScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        string s = new(Bstrs.Chars(bstr));
        result.ReleaseUntaken();
        return s;
    }

    [Theory]
    [InlineData("accdb", DbVersion120)]
    [InlineData("mdb", DbVersion40)]
    public void RQ53_DAO_scalar_conversions_and_refusals_match_oracle_records(string ext, int dbVersion)
    {
        // evidence/r-q53-conv-abspos-subq-loc/conv-dao-scalars-accdb, conv-dao-scalars-mdb
        string path = Path.Combine(_dir, $"conv-dao.{ext}");
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, dbVersion));
        Assert.Null(Method(created, "Close").Failure);

        AdoConnectionObject cn = CreateConnection();
        Assert.Null(Put(cn, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
        Assert.Null(Put(cn, "Mode", 3).Failure);
        Assert.Null(Method(cn, "Open", path, null, null, null).Failure);
        Assert.Null(Method(cn, "Execute",
            "CREATE TABLE TC (ID LONG CONSTRAINT PK_TC PRIMARY KEY, B BYTE, I SMALLINT, L INTEGER, S REAL, D FLOAT, C CURRENCY, DT DATETIME, Y BIT, DC DECIMAL(18,4), T TEXT(40), M MEMO, G GUID)",
            -1, 129).Failure);
        Assert.Null(Method(cn, "Close").Failure);

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        RecordsetObject rs = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TC", DbOpenDynaset));
        FieldsObject fs = Recover<FieldsObject>(Get(rs, "Fields"));

        FieldObject F(string name) => Recover<FieldObject>(Get(fs, "Item", name));

        // Row 1: valid text -> scalars, numeric -> Text/Memo, braced GUID
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(F("ID"), "Value", 1).Failure);
        Assert.Null(Put(F("B"), "Value", "42").Failure);
        Assert.Null(Put(F("I"), "Value", "-123").Failure);
        Assert.Null(Put(F("L"), "Value", "100000").Failure);
        Assert.Null(Put(F("S"), "Value", "3.5").Failure);
        Assert.Null(Put(F("D"), "Value", "-12.25").Failure);
        Assert.Null(Put(F("DT"), "Value", "2026-03-15").Failure);
        Assert.Null(Put(F("Y"), "Value", "True").Failure);
        Assert.Null(Put(F("DC"), "Value", "12.3456").Failure);
        Assert.Null(Put(F("T"), "Value", 12345).Failure);
        Assert.Null(Put(F("M"), "Value", 12.5).Failure);
        Assert.Null(Put(F("G"), "Value", "{00112233-4455-6677-8899-AABBCCDDEEFF}").Failure);
        Assert.Null(Method(rs, "Update").Failure);

        // Row 2: half-to-even rounding + bool/currency -> Text/Memo + 3759 over-scale DECIMAL
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(F("ID"), "Value", 2).Failure);
        Assert.Null(Put(F("B"), "Value", "2.5").Failure);
        Assert.Null(Put(F("I"), "Value", "0.5").Failure);
        Assert.Null(Put(F("L"), "Value", 2147483646.5).Failure);
        Assert.Null(Put(F("C"), "Value", "0.00005").Failure);
        Assert.Null(Put(F("Y"), "Value", "False").Failure);
        CallResult overScale = Put(F("DC"), "Value", "12.34565");
        Assert.Equal(3759, overScale.Failure?.Number);
        Assert.Equal("DAO.Field", overScale.Failure?.Source);
        Assert.Null(Put(F("T"), "Value", true).Failure);
        Assert.Null(Put(F("M"), "Value", new CurrencyArg(456700)).Failure);
        Assert.Null(Method(rs, "Update").Failure);

        // Row 3: VT_EMPTY -> Null (False on Yes/No)
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(F("ID"), "Value", 3).Failure);
        Assert.Null(Put(F("B"), "Value", new EmptyArg()).Failure);
        Assert.Equal(VarType.Null, TypeOf(Get(F("B"), "Value")));
        Assert.Null(Put(F("Y"), "Value", new EmptyArg()).Failure);
        Assert.False(BoolOf(Get(F("Y"), "Value")));
        Assert.Null(Put(F("T"), "Value", new EmptyArg()).Failure);
        Assert.Equal(VarType.Null, TypeOf(Get(F("T"), "Value")));
        Assert.Null(Put(F("G"), "Value", new EmptyArg()).Failure);
        Assert.Equal(VarType.Null, TypeOf(Get(F("G"), "Value")));
        Assert.Null(Method(rs, "Update").Failure);

        // Row 4: Null -> Yes/No stores False
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(F("ID"), "Value", 4).Failure);
        Assert.Null(Put(F("Y"), "Value", DBNull.Value).Failure);
        Assert.False(BoolOf(Get(F("Y"), "Value")));
        Assert.Null(Method(rs, "Update").Failure);

        // Refusal matrix: bad text / overflow / non-BSTR GUID -> 3421; over-precision DECIMAL -> 3761; Missing -> 0x80020004
        Assert.Null(Method(rs, "AddNew").Failure);
        Assert.Null(Put(F("ID"), "Value", 99).Failure);
        Assert.Equal(3421, Put(F("B"), "Value", "abc").Failure?.Number);
        Assert.Equal(3421, Put(F("B"), "Value", "").Failure?.Number);
        Assert.Equal(3421, Put(F("I"), "Value", "40000").Failure?.Number);
        Assert.Equal(3421, Put(F("L"), "Value", "abc").Failure?.Number);
        Assert.Equal(3421, Put(F("S"), "Value", "1e39").Failure?.Number);
        Assert.Equal(3421, Put(F("DC"), "Value", "abc").Failure?.Number);
        Assert.Equal(3761, Put(F("DC"), "Value", "999999999999999.0").Failure?.Number);
        Assert.Equal(3761, Put(F("DC"), "Value", 1000000000000000.0).Failure?.Number);
        CallResult missingPut = Put(F("T"), "Value", new MissingArg());
        Assert.Equal(unchecked((int)0x80020004), missingPut.Failure?.Scode);
        Assert.True(missingPut.Failure?.NullExcepStrings);
        Assert.Equal(3421, Put(F("G"), "Value", 1).Failure?.Number);
        Assert.Equal(3421, Put(F("G"), "Value", new byte[16]).Failure?.Number);
        Assert.Null(Method(rs, "CancelUpdate").Failure);
        Assert.Null(Method(rs, "Close").Failure);

        // Re-read persisted rows
        RecordsetObject r2 = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TC", DbOpenSnapshot));
        FieldsObject fs2 = Recover<FieldsObject>(Get(r2, "Fields"));
        FieldObject F2(string name) => Recover<FieldObject>(Get(fs2, "Item", name));

        // Row 1
        Assert.Equal(1, IntOf(Get(F2("ID"), "Value")));
        Assert.Equal(42, IntOf(Get(F2("B"), "Value")));
        Assert.Equal(-123, (short)IntOf(Get(F2("I"), "Value")));
        Assert.Equal(100000, IntOf(Get(F2("L"), "Value")));
        Assert.True(BoolOf(Get(F2("Y"), "Value")));
        Assert.Equal("12345", StrOf(Get(F2("T"), "Value")));
        Assert.Equal("12.5", StrOf(Get(F2("M"), "Value")));
        Assert.Equal("{guid {00112233-4455-6677-8899-AABBCCDDEEFF}}", StrOf(Get(F2("G"), "Value")));

        // Row 2
        Assert.Null(Method(r2, "MoveNext").Failure);
        Assert.Equal(2, IntOf(Get(F2("ID"), "Value")));
        Assert.Equal(2, IntOf(Get(F2("B"), "Value")));
        Assert.Equal(0, (short)IntOf(Get(F2("I"), "Value")));
        Assert.Equal(2147483646, IntOf(Get(F2("L"), "Value")));
        Assert.Equal(0L, LongOf(Get(F2("C"), "Value")));
        Assert.False(BoolOf(Get(F2("Y"), "Value")));
        Assert.Equal("-1", StrOf(Get(F2("T"), "Value")));
        Assert.Equal("45.67", StrOf(Get(F2("M"), "Value")));

        // Row 3 & 4
        Assert.Null(Method(r2, "MoveNext").Failure);
        Assert.Equal(VarType.Null, TypeOf(Get(F2("B"), "Value")));
        Assert.False(BoolOf(Get(F2("Y"), "Value")));
        Assert.Null(Method(r2, "MoveNext").Failure);
        Assert.False(BoolOf(Get(F2("Y"), "Value")));

        Assert.Null(Method(r2, "Close").Failure);
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData("accdb", DbVersion120)]
    [InlineData("mdb", DbVersion40)]
    public void RQ54_Field_Properties_AppendOnly_and_FieldSize_on_dynaset_and_snapshot(string ext, int dbVersion)
    {
        // evidence/r-q54-excl-ddl-ix-cmd/field-meta-and-lockwait-accdb, field-meta-and-lockwait-mdb
        // calls 65[17], 70[17], 75[17], 75[19], 80[17], 97[17]
        string path = Path.Combine(_dir, $"field-meta.{ext}");
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, dbVersion));
        Assert.Null(Method(created, "Close").Failure);

        AdoConnectionObject cn = CreateConnection();
        Assert.Null(Put(cn, "Provider", "Microsoft.ACE.OLEDB.12.0").Failure);
        Assert.Null(Put(cn, "Mode", 3).Failure);
        Assert.Null(Method(cn, "Open", path, null, null, null).Failure);
        Assert.Null(Method(cn, "Execute",
            "CREATE TABLE TMeta (ID LONG CONSTRAINT PK_TM PRIMARY KEY, DC DECIMAL(18,4), T TEXT(20), OL LONGBINARY, G GUID)",
            -1, 129).Failure);
        Assert.Null(Method(cn, "Close").Failure);

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        RecordsetObject dyn = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TMeta", DbOpenDynaset));
        FieldsObject fs = Recover<FieldsObject>(Get(dyn, "Fields"));
        Assert.Null(Method(dyn, "AddNew").Failure);
        Assert.Null(Put(Recover<FieldObject>(Get(fs, "Item", "ID")), "Value", 1).Failure);
        Assert.Null(Put(Recover<FieldObject>(Get(fs, "Item", "DC")), "Value", "12.34").Failure);
        Assert.Null(Put(Recover<FieldObject>(Get(fs, "Item", "T")), "Value", "abc").Failure);
        if (ext == "accdb")
        {
            Assert.Null(Put(Recover<FieldObject>(Get(fs, "Item", "OL")), "Value", new byte[] { 1, 2, 3, 4, 5, 6 }).Failure);
        }
        Assert.Null(Put(Recover<FieldObject>(Get(fs, "Item", "G")), "Value", "{00112233-4455-6677-8899-AABBCCDDEEFF}").Failure);
        Assert.Null(Method(dyn, "Update").Failure);
        Assert.Null(Method(dyn, "MoveFirst").Failure);

        // Dynaset: AppendOnly = False on DC, T, OL, G; FieldSize = 6 on populated OL (0 on Null OL), 3259 on non-LV
        foreach (string col in new[] { "DC", "T", "OL", "G" })
        {
            FieldObject f = Recover<FieldObject>(Get(fs, "Item", col));
            PropertiesObject props = Recover<PropertiesObject>(Get(f, "Properties"));
            PropertyObject appOnly = Recover<PropertyObject>(Get(props, "Item", "AppendOnly"));
            Assert.False(BoolOf(Get(appOnly, "Value")));

            PropertyObject fSize = Recover<PropertyObject>(Get(props, "Item", "FieldSize"));
            CallResult fSizeVal = Get(fSize, "Value");
            if (col == "OL")
            {
                Assert.Equal(ext == "accdb" ? 6 : 0, IntOf(fSizeVal));
            }
            else
            {
                Assert.Equal(3259, fSizeVal.Failure?.Number);
            }
        }
        Assert.Null(Method(dyn, "Close").Failure);

        // Snapshot: AppendOnly = False on DC (call 97[17])
        RecordsetObject snp = Recover<RecordsetObject>(Method(db, "OpenRecordset", "TMeta", DbOpenSnapshot));
        FieldObject snpDc = Recover<FieldObject>(Get(Recover<FieldsObject>(Get(snp, "Fields")), "Item", "DC"));
        PropertyObject snpAppOnly = Recover<PropertyObject>(Get(Recover<PropertiesObject>(Get(snpDc, "Properties")), "Item", "AppendOnly"));
        Assert.False(BoolOf(Get(snpAppOnly, "Value")));
        Assert.Null(Method(snp, "Close").Failure);
        Assert.Null(Method(db, "Close").Failure);
    }
}
