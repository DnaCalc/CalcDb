using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// COM DAO tests for <c>TableDef.CreateIndex</c>, <c>Indexes.Append</c>, <c>Indexes.Delete</c>, exclusive attached
/// <c>TableDef.Fields.Append</c>, and draft <c>dbDecimal (20)</c> <c>CreateField</c> on ACCDB &amp; Jet 4 MDB
/// (W-07, W-08 / R-CAT-1, R-DAO-1; <c>r-q52-ddl-make-domain</c>, <c>r-q53-conv-abspos-subq-loc</c>,
/// <c>r-q54-excl-ddl-ix-cmd</c>).
/// </summary>
public sealed unsafe class DaoCreateIndexTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion40 = 64;
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;
    private const int DbDescending = 1;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-idx-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly Dictionary<ComObject, nint> _ptrs = new(ReferenceEqualityComparer.Instance);
    private readonly EngineState _engine = new(new HostBinding(0xDA0_1D52, Standalone: true, 0, 0, 0, 0, 0));

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

    private string CreateEmptyDb(string fileName, int version)
    {
        string path = Path.Combine(_dir, fileName);
        DBEngineObject eng = CreateEngine();
        DatabaseObject created = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, version));
        Assert.Null(Method(created, "Close").Failure);
        return path;
    }

    [Theory]
    [InlineData("idx-empty-pop.accdb", DbVersion120)]
    [InlineData("idx-empty-pop.mdb", DbVersion40)]
    public void Attached_and_draft_create_index_on_empty_and_populated_tables(string fileName, int version)
    {
        string path = CreateEmptyDb(fileName, version);
        DBEngineObject eng = CreateEngine();

        // Setup table with rows, including Nulls and composite ordering.
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG, K TEXT(20), N LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (1, 'b', 20)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (2, 'a', 10)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (3, Null, 30)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Append Primary, Unique+IgnoreNulls, Required, and composite DESC indexes in fresh sessions.
        db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DispatchObject tds = Recover<DispatchObject>(Get(db, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DispatchObject idxs = Recover<DispatchObject>(Get(td, "Indexes"));

        NewIndexObject pk = Recover<NewIndexObject>(Method(td, "CreateIndex", "PK_T"));
        Assert.Null(Put(pk, "Primary", true).Failure);
        Assert.True(BoolOf(Get(pk, "Primary")));
        NewIndexFieldsObject pkFields = Recover<NewIndexFieldsObject>(Get(pk, "Fields"));
        NewIndexFieldObject pkF = Recover<NewIndexFieldObject>(Method(pk, "CreateField", "ID"));
        Assert.Null(Method(pkFields, "Append", pkF).Failure);
        Assert.Null(Method(idxs, "Append", pk).Failure);

        NewIndexObject uniqIgn = Recover<NewIndexObject>(Method(td, "CreateIndex", "IX_K_UniqIgn"));
        Assert.Null(Put(uniqIgn, "Unique", true).Failure);
        Assert.Null(Put(uniqIgn, "IgnoreNulls", true).Failure);
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(uniqIgn, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(uniqIgn, "CreateField", "K"))).Failure);
        Assert.Null(Method(idxs, "Append", uniqIgn).Failure);

        NewIndexObject comp = Recover<NewIndexObject>(Method(td, "CreateIndex", "IX_Comp"));
        Assert.Null(Put(comp, "Unique", true).Failure);
        NewIndexFieldsObject compFields = Recover<NewIndexFieldsObject>(Get(comp, "Fields"));
        NewIndexFieldObject fK = Recover<NewIndexFieldObject>(Method(comp, "CreateField", "K"));
        NewIndexFieldObject fN = Recover<NewIndexFieldObject>(Method(comp, "CreateField", "N"));
        Assert.Null(Put(fN, "Attributes", DbDescending).Failure);
        Assert.Null(Method(compFields, "Append", fK).Failure);
        Assert.Null(Method(compFields, "Append", fN).Failure);
        Assert.Null(Method(idxs, "Append", comp).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Draft TableDef with PK_Draft appended before TableDefs.Append (fresh session).
        db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        tds = Recover<DispatchObject>(Get(db, "TableDefs"));
        NewTableDefObject draftTd = Recover<NewTableDefObject>(Method(db, "CreateTableDef", "TDraft"));
        NewFieldsObject draftFs = Recover<NewFieldsObject>(Get(draftTd, "Fields"));
        Assert.Null(Method(draftFs, "Append", Recover<NewFieldObject>(Method(draftTd, "CreateField", "ID", 4))).Failure);
        Assert.Null(Method(draftFs, "Append", Recover<NewFieldObject>(Method(draftTd, "CreateField", "S", 10, 20))).Failure);
        NewIndexesObject draftIdxs = Recover<NewIndexesObject>(Get(draftTd, "Indexes"));
        NewIndexObject draftPk = Recover<NewIndexObject>(Method(draftTd, "CreateIndex", "PK_Draft"));
        Assert.Null(Put(draftPk, "Primary", true).Failure);
        NewIndexFieldsObject draftPkFs = Recover<NewIndexFieldsObject>(Get(draftPk, "Fields"));
        Assert.Null(Method(draftPkFs, "Append", Recover<NewIndexFieldObject>(Method(draftPk, "CreateField", "ID"))).Failure);
        Assert.Null(Method(draftIdxs, "Append", draftPk).Failure);
        Assert.Null(Method(tds, "Append", draftTd).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Verify metadata on read-only reopen.
        DatabaseObject ro = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        DispatchObject roTds = Recover<DispatchObject>(Get(ro, "TableDefs"));
        TableDefObject roT = Recover<TableDefObject>(Get(roTds, "Item", "T"));
        DispatchObject roIdxs = Recover<DispatchObject>(Get(roT, "Indexes"));
        Assert.Equal(3, IntOf(Get(roIdxs, "Count")));
        IndexObject roPk = Recover<IndexObject>(Get(roIdxs, "Item", "PK_T"));
        Assert.True(BoolOf(Get(roPk, "Primary")));
        Assert.True(BoolOf(Get(roPk, "Unique")));
        Assert.True(BoolOf(Get(roPk, "Required")));
        IndexObject roUniqIgn = Recover<IndexObject>(Get(roIdxs, "Item", "IX_K_UniqIgn"));
        Assert.True(BoolOf(Get(roUniqIgn, "Unique")));
        Assert.True(BoolOf(Get(roUniqIgn, "IgnoreNulls")));
        IndexObject roComp = Recover<IndexObject>(Get(roIdxs, "Item", "IX_Comp"));
        Assert.True(BoolOf(Get(roComp, "Unique")));
        Assert.False(BoolOf(Get(roComp, "IgnoreNulls")));
        DispatchObject roCompFs = Recover<DispatchObject>(Get(roComp, "Fields"));
        Assert.Equal(2, IntOf(Get(roCompFs, "Count")));
        Assert.Equal(DbDescending, IntOf(Get(Recover<DispatchObject>(Get(roCompFs, "Item", 1)), "Attributes")));

        TableDefObject roDraft = Recover<TableDefObject>(Get(roTds, "Item", "TDraft"));
        DispatchObject roDraftIdxs = Recover<DispatchObject>(Get(roDraft, "Indexes"));
        Assert.Equal(1, IntOf(Get(roDraftIdxs, "Count")));
        Assert.Equal("PK_Draft", TextOf(Get(Recover<IndexObject>(Get(roDraftIdxs, "Item", 0)), "Name")));
        Assert.Null(Method(ro, "Close").Failure);
    }

    [Theory]
    [InlineData("idx-delete.accdb", DbVersion120)]
    [InlineData("idx-delete.mdb", DbVersion40)]
    public void Delete_aliased_and_physical_indexes_and_invalidate_held_objects(string fileName, int version)
    {
        string path = CreateEmptyDb(fileName, version);
        DBEngineObject eng = CreateEngine();

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, K TEXT(20), N LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (1, 'b', 20)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (2, 'a', 10)", DbFailOnError).Failure);
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(db, "TableDefs")), "Item", "T"));
        DispatchObject idxs = Recover<DispatchObject>(Get(td, "Indexes"));

        // IX_K and IX_Alias share the same physical index.
        foreach (string name in new[] { "IX_K", "IX_Alias" })
        {
            NewIndexObject ix = Recover<NewIndexObject>(Method(td, "CreateIndex", name));
            NewIndexFieldsObject fs = Recover<NewIndexFieldsObject>(Get(ix, "Fields"));
            Assert.Null(Method(fs, "Append", Recover<NewIndexFieldObject>(Method(ix, "CreateField", "K"))).Failure);
            Assert.Null(Method(idxs, "Append", ix).Failure);
        }
        Assert.Null(Method(db, "Close").Failure);

        // Delete IX_Alias (shared physical), then IX_K (physical), then PK_T (last).
        db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        td = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(db, "TableDefs")), "Item", "T"));
        idxs = Recover<DispatchObject>(Get(td, "Indexes"));
        IndexObject heldAlias = Recover<IndexObject>(Get(idxs, "Item", "IX_Alias"));
        DispatchObject heldFields = Recover<DispatchObject>(Get(heldAlias, "Fields"));
        DispatchObject heldField = Recover<DispatchObject>(Get(heldFields, "Item", 0));

        Assert.Null(Method(idxs, "Delete", "IX_Alias").Failure);
        Assert.Equal(3420, Get(heldAlias, "Name").Failure?.Number);
        Assert.Equal(3420, Get(heldFields, "Count").Failure?.Number);
        Assert.Equal(3420, Get(heldField, "Name").Failure?.Number);

        Assert.Null(Method(idxs, "Delete", "IX_K").Failure);
        Assert.Null(Method(idxs, "Delete", "PK_T").Failure);
        Assert.Equal(0, IntOf(Get(idxs, "Count")));
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData("idx-errors.accdb", DbVersion120)]
    [InlineData("idx-errors.mdb", DbVersion40)]
    public void Index_error_matrix_matches_ace_error_numbers(string fileName, int version)
    {
        string path = CreateEmptyDb(fileName, version);
        DBEngineObject eng = CreateEngine();

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE Parent (PID LONG CONSTRAINT PK_P PRIMARY KEY, U TEXT(20), D LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE TABLE Child (CID LONG CONSTRAINT PK_C PRIMARY KEY, PID LONG CONSTRAINT FK_C_P REFERENCES Parent (PID))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO Parent VALUES (1, 'x', 10)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO Parent VALUES (2, 'x', 20)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO Parent VALUES (3, Null, 30)", DbFailOnError).Failure);
        TableDefObject parentTd = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(db, "TableDefs")), "Item", "Parent"));
        DispatchObject parentIdxs = Recover<DispatchObject>(Get(parentTd, "Indexes"));
        NewIndexObject ixD = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_D"));
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(ixD, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(ixD, "CreateField", "D"))).Failure);
        Assert.Null(Method(parentIdxs, "Append", ixD).Failure);
        Assert.Null(Method(db, "Close").Failure);

        db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        DispatchObject tds = Recover<DispatchObject>(Get(db, "TableDefs"));
        parentTd = Recover<TableDefObject>(Get(tds, "Item", "Parent"));
        parentIdxs = Recover<DispatchObject>(Get(parentTd, "Indexes"));

        // 3264: 0 fields
        Assert.Equal(3264, Method(parentIdxs, "Append", Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_Empty"))).Failure?.Number);

        // 3125: empty name
        NewIndexObject emptyName = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", ""));
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(emptyName, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(emptyName, "CreateField", "D"))).Failure);
        Assert.Equal(3125, Method(parentIdxs, "Append", emptyName).Failure?.Number);

        // 3284: duplicate index name
        NewIndexObject dupName = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_D"));
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(dupName, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(dupName, "CreateField", "U"))).Failure);
        Assert.Equal(3284, Method(parentIdxs, "Append", dupName).Failure?.Number);

        // 3283: second Primary key
        NewIndexObject pk2 = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "PK_2"));
        Assert.Null(Put(pk2, "Primary", true).Failure);
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(pk2, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(pk2, "CreateField", "D"))).Failure);
        Assert.Equal(3283, Method(parentIdxs, "Append", pk2).Failure?.Number);

        // 3409: missing column
        NewIndexObject missingCol = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_Missing"));
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(missingCol, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(missingCol, "CreateField", "NoSuchCol"))).Failure);
        Assert.Equal(3409, Method(parentIdxs, "Append", missingCol).Failure?.Number);

        // 3285: duplicate column in index
        NewIndexObject dupCol = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_DupCol"));
        NewIndexFieldsObject dupColFs = Recover<NewIndexFieldsObject>(Get(dupCol, "Fields"));
        Assert.Null(Method(dupColFs, "Append", Recover<NewIndexFieldObject>(Method(dupCol, "CreateField", "D"))).Failure);
        Assert.Null(Method(dupColFs, "Append", Recover<NewIndexFieldObject>(Method(dupCol, "CreateField", "D"))).Failure);
        Assert.Equal(3285, Method(parentIdxs, "Append", dupCol).Failure?.Number);

        // 3022: unique over duplicate 'x'
        NewIndexObject uxDup = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "UX_Dup"));
        Assert.Null(Put(uxDup, "Unique", true).Failure);
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(uxDup, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(uxDup, "CreateField", "U"))).Failure);
        Assert.Equal(3022, Method(parentIdxs, "Append", uxDup).Failure?.Number);

        // 3058: Required over Null
        NewIndexObject ixReq = Recover<NewIndexObject>(Method(parentTd, "CreateIndex", "IX_Req"));
        Assert.Null(Put(ixReq, "Required", true).Failure);
        Assert.Null(Method(Recover<NewIndexFieldsObject>(Get(ixReq, "Fields")), "Append", Recover<NewIndexFieldObject>(Method(ixReq, "CreateField", "U"))).Failure);
        Assert.Equal(3058, Method(parentIdxs, "Append", ixReq).Failure?.Number);

        // 3268 / 3367 / 3219 on appended Index
        IndexObject appIx = Recover<IndexObject>(Get(parentIdxs, "Item", "IX_D"));
        Assert.Equal(3268, Put(appIx, "Unique", true).Failure?.Number);
        Assert.Equal(3367, Method(parentIdxs, "Append", appIx).Failure?.Number);
        DispatchObject appIxFs = Recover<DispatchObject>(Get(appIx, "Fields"));
        Assert.Equal(3219, Method(appIxFs, "Append", Recover<NewIndexFieldObject>(Method(appIx, "CreateField", "U"))).Failure?.Number);

        // 3280 (Fields.Delete indexed col), 3281 (Indexes.Delete PK in relation), 3265 (Delete missing)
        DispatchObject parentFs = Recover<DispatchObject>(Get(parentTd, "Fields"));
        Assert.Equal(3280, Method(parentFs, "Delete", "D").Failure?.Number);
        Assert.Equal(3281, Method(parentIdxs, "Delete", "PK_P").Failure?.Number);
        Assert.Equal(3265, Method(parentIdxs, "Delete", "NoSuchIndex").Failure?.Number);
        Assert.Null(Method(db, "Close").Failure);

        // 3027: read-only database
        DatabaseObject ro = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, true));
        TableDefObject roTd = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(ro, "TableDefs")), "Item", "Parent"));
        DispatchObject roIdxs = Recover<DispatchObject>(Get(roTd, "Indexes"));
        Assert.Equal(3027, Method(roIdxs, "Delete", "IX_D").Failure?.Number);
        Assert.Null(Method(ro, "Close").Failure);
    }

    [Theory]
    [InlineData("excl-fields-append.accdb", DbVersion120)]
    [InlineData("excl-fields-append.mdb", DbVersion40)]
    public void Exclusive_attached_tabledef_fields_append_and_3191_duplicate(string fileName, int version)
    {
        string path = CreateEmptyDb(fileName, version);
        DBEngineObject eng = CreateEngine();

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, true, false));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "INSERT INTO T VALUES (1)", DbFailOnError).Failure);
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(db, "TableDefs")), "Item", "T"));
        DispatchObject fs = Recover<DispatchObject>(Get(td, "Fields"));

        Assert.Null(Method(fs, "Append", Recover<NewFieldObject>(Method(td, "CreateField", "Num", 4))).Failure);
        Assert.Null(Method(fs, "Append", Recover<NewFieldObject>(Method(td, "CreateField", "Txt", 10, 40))).Failure);
        Assert.Null(Method(fs, "Append", Recover<NewFieldObject>(Method(td, "CreateField", "Notes", 12))).Failure);
        Assert.Equal(3191, Method(fs, "Append", Recover<NewFieldObject>(Method(td, "CreateField", "Num", 4))).Failure?.Number);
        Assert.Equal(4, IntOf(Get(fs, "Count")));
        Assert.Null(Method(db, "Close").Failure);
    }

    [Theory]
    [InlineData("draft-decimal.accdb", DbVersion120)]
    [InlineData("draft-decimal.mdb", DbVersion40)]
    public void Draft_decimal_createfield_persists_precision_38_and_fails_dao_read_3000(string fileName, int version)
    {
        string path = CreateEmptyDb(fileName, version);
        DBEngineObject eng = CreateEngine();

        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        NewTableDefObject td = Recover<NewTableDefObject>(Method(db, "CreateTableDef", "TDraftDec"));
        NewFieldsObject fs = Recover<NewFieldsObject>(Get(td, "Fields"));
        Assert.Null(Method(fs, "Append", Recover<NewFieldObject>(Method(td, "CreateField", "ID", 4))).Failure);

        NewFieldObject fDec = Recover<NewFieldObject>(Method(td, "CreateField", "FDec", 20, 16));
        Assert.Equal(16, IntOf(Get(fDec, "Type")));
        Assert.Equal(8, IntOf(Get(fDec, "Size")));
        Assert.Equal(0, IntOf(Get(fDec, "Attributes")));
        Assert.Null(Put(fDec, "Required", true).Failure);
        Assert.Equal(3219, Put(fDec, "AllowZeroLength", true).Failure?.Number);
        Assert.False(BoolOf(Get(fDec, "AllowZeroLength")));
        Assert.Null(Method(fs, "Append", fDec).Failure);
        Assert.Null(Method(Recover<DispatchObject>(Get(db, "TableDefs")), "Append", td).Failure);
        Assert.Null(Method(db, "Close").Failure);

        // Reopen to verify persisted metadata, SQL INSERT, and DAO read 3000.
        db = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        TableDefObject persisted = Recover<TableDefObject>(Get(Recover<DispatchObject>(Get(db, "TableDefs")), "Item", "TDraftDec"));
        DispatchObject persistedDec = Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(persisted, "Fields")), "Item", "FDec"));
        Assert.Equal(20, IntOf(Get(persistedDec, "Type")));
        Assert.Equal(20, IntOf(Get(persistedDec, "Size")));
        Assert.Equal(1, IntOf(Get(persistedDec, "Attributes")));
        Assert.True(BoolOf(Get(persistedDec, "Required")));
        Assert.Equal(38, IntOf(Get(persistedDec, "Precision")));
        Assert.Equal(0, IntOf(Get(persistedDec, "Scale")));

        // SQL INSERT succeeds; DAO read of non-Null Decimal(38) fails 3000.
        Assert.Null(Method(db, "Execute", "INSERT INTO TDraftDec (ID, FDec) VALUES (1, 123)", DbFailOnError).Failure);
        DispatchObject rs = Recover<DispatchObject>(Method(db, "OpenRecordset", "SELECT ID, FDec FROM TDraftDec"));
        DispatchObject rsDec = Recover<DispatchObject>(Get(Recover<DispatchObject>(Get(rs, "Fields")), "Item", "FDec"));
        Assert.Equal(3000, Get(rsDec, "Value").Failure?.Number);
        Assert.Null(Method(rs, "Close").Failure);
        Assert.Null(Method(db, "Close").Failure);
    }
}
