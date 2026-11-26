using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// COM DAO tests for <c>TableDef.CreateField</c> (draft &amp; attached), <c>Fields.Append</c>,
/// <c>Database.CreateRelation</c>, <c>Relations.Append</c>, and <c>Relations.Delete</c> on ACCDB &amp; Jet 4 MDB
/// (W-07 / R-CAT-1, R-DAO-1; <c>r-q46-dao-ddl-multi</c>, <c>r-q47-bigint-expr-edges</c>).
/// </summary>
public sealed unsafe class DaoCreateFieldAndRelationTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-cf-rel-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(0xDA0_CF46, Standalone: true, 0, 0, 0, 0, 0));

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

    private static string TextOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        nint ptr = result.HasScalar ? result.Scalar.Pointer : result.Value!.Pointer->Pointer;
        string s = Marshal.PtrToStringBSTR(ptr);
        result.ReleaseUntaken();
        return s;
    }

    [Theory]
    [InlineData("draft.accdb", DbVersion120)]
    [InlineData("draft.mdb", DbVersion40)]
    public void Draft_createfield_all_13_non_decimal_types_and_refusals(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(created, "Close").Failure);
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        NewTableDefObject td = Recover<NewTableDefObject>(Method(db, "CreateTableDef", "AllTypes"));
        NewFieldsObject tfs = Recover<NewFieldsObject>(Get(td, "Fields"));

        // Refusals: 9999 -> 3259, complex 101 -> NotModelled, BigInt 16 -> NotModelled, draft Decimal 20 -> NotModelled
        Failure? f9999 = Method(td, "CreateField", "Bad", 9999).Failure;
        Assert.Equal(3259, f9999?.Number);
        Assert.StartsWith("NotModelled.DaoCreateFieldComplexType", Method(td, "CreateField", "Cplx", 101).Failure?.Description);
        Assert.StartsWith("NotModelled.DaoCreateFieldBigInt", Method(td, "CreateField", "BI", 16).Failure?.Description);
        Assert.StartsWith("NotModelled.DaoCreateFieldDraftDecimal", Method(td, "CreateField", "Dec", 20).Failure?.Description);

        // AutoNumber (dbLong + Attributes=16)
        NewFieldObject fId = Recover<NewFieldObject>(Method(td, "CreateField", "Id", 4));
        Assert.Equal(4, IntOf(Get(fId, "Size")));
        Assert.Equal(0, IntOf(Get(fId, "Attributes")));
        Assert.Null(Put(fId, "Attributes", 16).Failure);
        Assert.Equal(16, IntOf(Get(fId, "Attributes")));
        Assert.Null(Method(tfs, "Append", fId).Failure);

        // Default-size dbText reads 0 on draft, reopens 255
        NewFieldObject fTxtDef = Recover<NewFieldObject>(Method(td, "CreateField", "TxtDef", 10));
        Assert.Equal(0, IntOf(Get(fTxtDef, "Size")));
        Assert.Null(Method(tfs, "Append", fTxtDef).Failure);

        // dbGUID reads Size=16, Attributes=0 on draft, reopens Attributes=1 (dbFixedField)
        NewFieldObject fGuid = Recover<NewFieldObject>(Method(td, "CreateField", "G", 15));
        Assert.Equal(16, IntOf(Get(fGuid, "Size")));
        Assert.Null(Method(tfs, "Append", fGuid).Failure);

        // dbBinary(16) reads Size=16, reopens variable (Attributes=2)
        NewFieldObject fBin = Recover<NewFieldObject>(Method(td, "CreateField", "Bin", 9, 16));
        Assert.Equal(16, IntOf(Get(fBin, "Size")));
        Assert.Null(Method(tfs, "Append", fBin).Failure);

        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        Assert.Null(Method(tds, "Append", td).Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject dbro = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        DefinitionCollection tds2 = Recover<DefinitionCollection>(Get(dbro, "TableDefs"));
        TableDefObject td2 = Recover<TableDefObject>(Get(tds2, "Item", "AllTypes"));
        DefinitionCollection fls2 = Recover<DefinitionCollection>(Get(td2, "Fields"));

        DefinitionFieldObject rId = Recover<DefinitionFieldObject>(Get(fls2, "Item", "Id"));
        Assert.Equal(17, IntOf(Get(rId, "Attributes")));
        DefinitionFieldObject rTxt = Recover<DefinitionFieldObject>(Get(fls2, "Item", "TxtDef"));
        Assert.Equal(255, IntOf(Get(rTxt, "Size")));
        DefinitionFieldObject rGuid = Recover<DefinitionFieldObject>(Get(fls2, "Item", "G"));
        Assert.Equal(1, IntOf(Get(rGuid, "Attributes")));
        Assert.Equal(16, IntOf(Get(rGuid, "Size")));
        DefinitionFieldObject rBin = Recover<DefinitionFieldObject>(Get(fls2, "Item", "Bin"));
        Assert.Equal(2, IntOf(Get(rBin, "Attributes")));
        Assert.Equal(16, IntOf(Get(rBin, "Size")));
        Assert.Null(Method(dbro, "Close").Failure);
    }

    [Theory]
    [InlineData("attached.accdb", DbVersion120)]
    [InlineData("attached.mdb", DbVersion40)]
    public void Attached_createfield_scalar_decimal_identity_and_3191(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK PRIMARY KEY)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db2, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DefinitionCollection fls = Recover<DefinitionCollection>(Get(td, "Fields"));

        NewFieldObject fLong = Recover<NewFieldObject>(Method(td, "CreateField", "ZLong", 4));
        Assert.Null(Put(fLong, "Required", true).Failure);
        Assert.Null(Method(fls, "Append", fLong).Failure);
        Assert.Equal(2, IntOf(Get(fls, "Count")));
        // COM identity preserved after attached Fields.Append (r-dao-1b/coll-append-field-existing-rows)
        // and attached NewFieldObject delegates full Field3 surface (CollectionIndex, Scale, Properties).
        NewFieldObject sameLong = Recover<NewFieldObject>(Get(fls, "Item", "ZLong"));
        Assert.Same(fLong, sameLong);
        Assert.True(BoolOf(Get(sameLong, "Required")));
        Assert.Equal(0, IntOf(Get(sameLong, "CollectionIndex")));
        Assert.Equal(0, IntOf(Get(sameLong, "Scale")));
        Assert.True(IntOf(Get(Recover<ComObject>(Get(sameLong, "Properties")) as DispatchObject ?? throw new InvalidOperationException(), "Count")) > 0);

        // Appending an already-attached NewFieldObject to a draft TableDef fails 3367.
        NewTableDefObject draftTd = Recover<NewTableDefObject>(Method(db2, "CreateTableDef", "DraftOther"));
        NewFieldsObject draftFls = Recover<NewFieldsObject>(Get(draftTd, "Fields"));
        Assert.Equal(3367, Method(draftFls, "Append", fLong).Failure?.Number);

        // Unprobed attached type (dbDouble) refuses by name with 0 bytes written.
        NewFieldObject fDbl = Recover<NewFieldObject>(Method(td, "CreateField", "ZDbl", 7));
        Assert.NotNull(Method(fls, "Append", fDbl).Failure);

        NewFieldObject fDec = Recover<NewFieldObject>(Method(td, "CreateField", "FDec", 20));
        Assert.Equal(16, IntOf(Get(fDec, "Type")));
        Assert.Equal(8, IntOf(Get(fDec, "Size")));
        Assert.Equal(0, IntOf(Get(fDec, "Attributes")));
        Assert.Null(Method(fls, "Append", fDec).Failure);

        // Case-insensitive duplicate fails 3191 from DAO.TableDef
        NewFieldObject fDup = Recover<NewFieldObject>(Method(td, "CreateField", "zlong", 4));
        Failure? dupFail = Method(fls, "Append", fDup).Failure;
        Assert.Equal(3191, dupFail?.Number);
        Assert.Equal("DAO.TableDef", dupFail?.Source);
        Assert.Null(Method(db2, "Close").Failure);

        DatabaseObject dbro = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        DefinitionCollection tds3 = Recover<DefinitionCollection>(Get(dbro, "TableDefs"));
        TableDefObject td3 = Recover<TableDefObject>(Get(tds3, "Item", "T"));
        DefinitionCollection fls3 = Recover<DefinitionCollection>(Get(td3, "Fields"));
        DefinitionFieldObject rDec = Recover<DefinitionFieldObject>(Get(fls3, "Item", "FDec"));
        Assert.Equal(20, IntOf(Get(rDec, "Type")));
        Assert.Equal(20, IntOf(Get(rDec, "Size")));
        Assert.Equal(1, IntOf(Get(rDec, "Attributes")));
        Assert.Null(Method(dbro, "Close").Failure);
    }

    [Theory]
    [InlineData("rels.accdb", DbVersion120)]
    [InlineData("rels.mdb", DbVersion40)]
    public void CreateRelation_append_delete_refresh_and_errors(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(db, "Execute", "CREATE TABLE P (ID LONG CONSTRAINT PK_P PRIMARY KEY)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE TABLE C (ID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG, S TEXT(10))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE TABLE COrph (ID LONG CONSTRAINT PK_CO PRIMARY KEY, PID LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO P VALUES (1)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO COrph VALUES (2, 99)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db2, "TableDefs"));
        TableDefObject tdC = Recover<TableDefObject>(Get(tds, "Item", "C"));
        DefinitionCollection idxC = Recover<DefinitionCollection>(Get(tdC, "Indexes"));
        Assert.Equal(1, IntOf(Get(idxC, "Count")));

        DefinitionCollection rels = Recover<DefinitionCollection>(Get(db2, "Relations"));

        // 3366 no fields
        NewRelationObject rEmpty = Recover<NewRelationObject>(Method(db2, "CreateRelation", "R_Empty", "P", "C", 0));
        Failure? f3366 = Method(rels, "Append", rEmpty).Failure;
        Assert.Equal(3366, f3366?.Number);
        Assert.Equal("DAO.Relations", f3366?.Source);

        // 3368 type mismatch (Long -> Text)
        NewRelationObject rMis = Recover<NewRelationObject>(Method(db2, "CreateRelation", "R_Mis", "P", "C", 0));
        NewRelationFieldObject rfMis = Recover<NewRelationFieldObject>(Method(rMis, "CreateField", "ID"));
        Assert.Null(Put(rfMis, "ForeignName", "S").Failure);
        NewRelationFieldsObject rfsMis = Recover<NewRelationFieldsObject>(Get(rMis, "Fields"));
        Assert.Null(Method(rfsMis, "Append", rfMis).Failure);
        Failure? f3368 = Method(rels, "Append", rMis).Failure;
        Assert.Equal(3368, f3368?.Number);

        // Succeeding cascade relation
        NewRelationObject rBoth = Recover<NewRelationObject>(Method(db2, "CreateRelation", "R_Both", "P", "C", 4352));
        NewRelationFieldObject rf = Recover<NewRelationFieldObject>(Method(rBoth, "CreateField", "ID"));
        Assert.Null(Put(rf, "ForeignName", "PID").Failure);
        NewRelationFieldsObject rfs = Recover<NewRelationFieldsObject>(Get(rBoth, "Fields"));
        Assert.Null(Method(rfs, "Append", rf).Failure);
        Assert.Null(Method(rels, "Append", rBoth).Failure);
        Assert.Equal(1, IntOf(Get(rels, "Count")));

        // Held C.Indexes stays stale until Refresh(), then shows PK_C + R_Both
        Assert.Equal(1, IntOf(Get(idxC, "Count")));
        Assert.Null(Method(idxC, "Refresh").Failure);
        Assert.Equal(2, IntOf(Get(idxC, "Count")));

        // 3012 duplicate name
        NewRelationObject rDup = Recover<NewRelationObject>(Method(db2, "CreateRelation", "r_both", "P", "C", 0));
        NewRelationFieldObject rfDup = Recover<NewRelationFieldObject>(Method(rDup, "CreateField", "ID"));
        Assert.Null(Put(rfDup, "ForeignName", "PID").Failure);
        NewRelationFieldsObject rfsDup = Recover<NewRelationFieldsObject>(Get(rDup, "Fields"));
        Assert.Null(Method(rfsDup, "Append", rfDup).Failure);
        Assert.Equal(3012, Method(rels, "Append", rDup).Failure?.Number);

        // Orphan row on COrph -> 3201 (while R_Both is still live so negative Id stays monotonic)
        NewRelationObject rOrph = Recover<NewRelationObject>(Method(db2, "CreateRelation", "R_Orph", "P", "COrph", 0));
        NewRelationFieldObject rfOrph = Recover<NewRelationFieldObject>(Method(rOrph, "CreateField", "ID"));
        Assert.Null(Put(rfOrph, "ForeignName", "PID").Failure);
        NewRelationFieldsObject rfsOrph = Recover<NewRelationFieldsObject>(Get(rOrph, "Fields"));
        Assert.Null(Method(rfsOrph, "Append", rfOrph).Failure);
        Failure? f3201 = Method(rels, "Append", rOrph).Failure;
        Assert.Equal(3201, f3201?.Number);
        Assert.Equal("You cannot add or change a record because a related record is required in table 'P'.", f3201?.Description);

        // Relations.Delete removes R_Both; 3265 on missing name; C.Indexes stays stale until Refresh()
        Assert.Null(Method(rels, "Delete", "R_Both").Failure);
        Assert.Equal(0, IntOf(Get(rels, "Count")));
        Assert.Equal(3265, Method(rels, "Delete", "R_Both").Failure?.Number);
        Assert.Null(Method(idxC, "Refresh").Failure);
        Assert.Equal(1, IntOf(Get(idxC, "Count")));

        Assert.Null(Method(db2, "Close").Failure);
    }
}
