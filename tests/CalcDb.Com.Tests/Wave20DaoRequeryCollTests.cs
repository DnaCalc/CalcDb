using System.IO.Compression;
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
/// Targeted tests for Wave 20 Slot 2 (<c>W-08</c>, <c>R-DAO-1</c>, <c>R-CURSOR-1</c>, <c>R-CAT-1</c>, <c>R-ERR-1</c>):
/// DAO <c>Requery</c> (table-type empty-to-populated <c>MoveLast</c>, dynaset <c>Requery</c> invalidating derived recordsets),
/// <c>r-str-ado-act</c> (<c>CancelUpdate</c> 3020, snapshot <c>FindFirst</c>/<c>FindPrevious</c>/<c>FindNext</c>,
/// <c>ALTER TABLE ADD COLUMN</c> 3211 beside open dynaset), and DAO collection / member / COM validation stops
/// (<c>TableDef.Fields.Append</c> deferred flush on <c>OpenRecordset</c>, saved <c>UNION</c> <c>QueryDef.OpenRecordset(4)</c>,
/// <c>OpenDatabase</c> alias path canonical <c>Name</c> + <c>\\?\</c> 3043, temporary <c>CreateQueryDef("", sql)</c> <c>TOP</c> 3141).
/// </summary>
public sealed unsafe class Wave20DaoRequeryCollTests : IDisposable
{
    private const string CursorMainAccdb = "47f8fa36bfcd0df6185061dc39e47fd46cdac20f372942f9a8ff3a177a8bfbf6";
    private const string StrAccdb = "22b81465269ae9b0dd729590d416c3778eb3385aaa2721a160665537a2cbc5a7";
    private const string DaoRichAccdb = "3b512c8f10e3ab22e434d9d6e69435bf231a310044b20cdb1c03069c1be34e95";
    private const string Q2CursorMainAccdb = "1473d5a4c26300ae8206b5c014d9179b424cca71dfbb02144ed21ce064d473c2";
    private const string Sql13bMainAccdb = "930a84695c32fb2984135ed60e828bf6f54288bcac39ced94ff8abeeb401100a";

    private readonly List<string> _files = [];
    private readonly List<DatabaseObject> _databases = [];
    private readonly List<nint> _cells = [];
    private readonly List<OwnedVariant> _ownedVariants = [];
    private readonly List<(ComObject Obj, nint Ptr)> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_2020, Standalone: true, 0, 0, 0, 0, 0));

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
        }
    }

    [Fact]
    public void TableCursor_requery_empty_to_populated_movelast_sees_inserted_rows()
    {
        // Replays evidence/r-cursor-1b/requery-empty-to-populated calls 5-33.
        DatabaseObject db = Open(CursorMainAccdb, readOnly: false);
        RecordsetObject rs = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "E", 1)));
        Assert.True(BoolValue(Ok(Get(rs, "BOF"))));
        Assert.True(BoolValue(Ok(Get(rs, "EOF"))));
        Assert.Equal(0, Int32Value(Ok(Get(rs, "RecordCount"))));

        Ok(Method(db, "Execute", "INSERT INTO E VALUES (1, 'a')", 128));
        AssertDaoError(Method(rs, "Requery"), 3251, "DAO.Recordset", errorInfo: false);
        Assert.Equal(1, Int32Value(Ok(Get(rs, "RecordCount"))));
        Ok(Method(rs, "MoveLast"));

        Ok(Method(db, "Execute", "INSERT INTO E VALUES (2, 'b')", 128));
        Ok(Method(rs, "MoveNext"));
        Assert.False(BoolValue(Ok(Get(rs, "BOF"))));
        Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
        Assert.Equal(2, Int32Value(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void Dynaset_requery_invalidates_derived_recordset_and_keeps_clone_requery()
    {
        // Replays evidence/r-cursor-1b/requery-family calls 5-73.
        DatabaseObject db = Open(CursorMainAccdb, readOnly: false);
        RecordsetObject rp = Recover<RecordsetObject>(Ok(Method(db, "OpenRecordset", "F", 2)));
        Ok(Method(rp, "Move", 2));
        RecordsetObject rc = Recover<RecordsetObject>(Ok(Method(rp, "Clone")));
        Ok(Method(rc, "Move", 1));
        SelectRecordsetObject rd = Recover<SelectRecordsetObject>(Ok(Method(rp, "OpenRecordset")));
        DispatchObject rdFields = Recover<DispatchObject>(Ok(Get(rd, "Fields")));
        DispatchObject dN = Recover<DispatchObject>(Ok(Get(rdFields, "Item", "N")));

        Ok(Method(rp, "Requery"));
        AssertDaoError(FieldValue(rc, "ID"), 3021, "DAO.Field", errorInfo: true);
        AssertDaoError(Get(dN, "Value"), 3021, "DAO.Field", errorInfo: true);
        Assert.Equal(1, Int32Value(Ok(Get(rd, "RecordCount"))));
        AssertDaoError(FieldValue(rd, "ID"), 3021, "DAO.Field", errorInfo: true);

        Ok(Method(db, "Execute", "INSERT INTO F (ID, N) VALUES (9, 90)", 128));
        Ok(Method(rc, "Requery"));
        Assert.Equal(1, Int32Value(Ok(Get(rp, "RecordCount"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rp, "ID"))));

        Ok(Method(rd, "Close"));
        Ok(Method(rc, "Close"));
        Ok(Method(rp, "Close"));
    }

    [Fact]
    public void SelectRecordset_dynaset_cancelupdate_without_edit_fails_3020()
    {
        // Replays evidence/r-str-ado-act/str-edit-requery calls 5-33.
        DatabaseObject db = Open(StrAccdb, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, T, M FROM S ORDER BY ID", 2)));
        Ok(Method(rs, "FindFirst", "ID = 6"));
        Ok(Method(rs, "Edit"));
        AssertDaoError(PutField(rs, "T", new string('z', 300)), 3163, "DAO.Field", errorInfo: true);
        Ok(Method(rs, "Update"));
        Assert.Equal(0, Int16Value(Ok(Get(rs, "EditMode"))));
        AssertDaoError(Method(rs, "CancelUpdate"), 3020, "DAO.Recordset", errorInfo: false);
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void SelectRecordset_snapshot_findfirst_and_getchunk_on_memo()
    {
        // Replays evidence/r-str-ado-act/str-memo-chunks calls 5-67.
        DatabaseObject db = Open(StrAccdb, readOnly: true);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, T, M FROM S ORDER BY ID", 4)));
        DispatchObject fl = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject ft = Recover<DispatchObject>(Ok(Get(fl, "Item", "T")));
        DispatchObject fm = Recover<DispatchObject>(Ok(Get(fl, "Item", "M")));

        Ok(Method(rs, "FindFirst", "ID = 3"));
        Assert.Equal("a\0b", TextValue(Ok(Method(fm, "GetChunk", 0, 16))));
        Ok(Method(rs, "FindFirst", "ID = 10"));
        Assert.Equal("0123456789012345", TextValue(Ok(Method(fm, "GetChunk", 0, 16))));
        Assert.Equal("90123", TextValue(Ok(Method(fm, "GetChunk", 7999, 5))));
        AssertDaoError(Method(ft, "GetChunk", 0, 4), 3259, "DAO.Field", errorInfo: false);
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void SelectRecordset_snapshot_findfirst_findprevious_findnext_revisit()
    {
        // Replays evidence/r-str-ado-act/str-revisit calls 5-60.
        DatabaseObject db = Open(StrAccdb, readOnly: true);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, T, M FROM S ORDER BY ID", 4)));
        Ok(Method(rs, "FindFirst", "ID = 10"));
        Assert.False(BoolValue(Ok(Get(rs, "NoMatch"))));
        Assert.Equal(10, Int32Value(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(9, Int32Value(Ok(Get(rs, "AbsolutePosition"))));

        Ok(Method(rs, "FindPrevious", "Len(M) > 1000"));
        Assert.False(BoolValue(Ok(Get(rs, "NoMatch"))));
        Assert.Equal(9, Int32Value(Ok(FieldValue(rs, "ID"))));
        Assert.Equal(8, Int32Value(Ok(Get(rs, "AbsolutePosition"))));

        Ok(Method(rs, "FindFirst", "InStr(T, Chr(0)) > 0"));
        Assert.Equal(3, Int32Value(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "FindNext", "InStr(T, Chr(0)) > 0"));
        Assert.Equal(4, Int32Value(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void AlterTable_add_column_beside_open_select_dynaset_fails_3211()
    {
        // Replays evidence/r-str-ado-act/str-schema-change calls 5-25.
        DatabaseObject db = Open(StrAccdb, readOnly: false);
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, T FROM S ORDER BY ID", 2)));
        AssertDaoError(Method(db, "Execute", "ALTER TABLE S ADD COLUMN X TEXT(10)"), 3211, "DAO.Database", errorInfo: false);
        Ok(Method(rs, "Close"));
        Ok(Method(db, "Execute", "ALTER TABLE S ADD COLUMN X TEXT(10)"));
    }

    [Fact]
    public void TableDef_fields_append_then_openrecordset_flushes_deferred_tabledef()
    {
        // Replays evidence/r-dao-1b/coll-append-field-existing-rows calls 4-25.
        DatabaseObject db = Open(DaoRichAccdb, readOnly: false);
        DispatchObject tds = Recover<DispatchObject>(Ok(Get(db, "TableDefs")));
        DispatchObject t = Recover<DispatchObject>(Ok(Get(tds, "Item", "Parent")));
        DispatchObject tf = Recover<DispatchObject>(Ok(Get(t, "Fields")));

        DispatchObject zopt = Recover<DispatchObject>(Ok(Method(t, "CreateField", "ZOpt", 4)));
        Ok(Method(tf, "Append", zopt));
        DispatchObject ztxt = Recover<DispatchObject>(Ok(Method(t, "CreateField", "ZTxt", 10, 10)));
        Ok(Put(ztxt, "AllowZeroLength", true));
        Ok(Method(tf, "Append", ztxt));

        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(db, "OpenRecordset", "SELECT ID, ZOpt, ZTxt FROM Parent ORDER BY ID", 4)));
        Assert.False(BoolValue(Ok(Get(rs, "EOF"))));
        Assert.Equal(1, Int32Value(Ok(FieldValue(rs, "ID"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void QueryDef_union_openrecordset_snapshot_members()
    {
        // Replays evidence/r-dao-1b/recordset-members-query-snapshot calls 4-48.
        DatabaseObject db = Open(DaoRichAccdb, readOnly: true);
        DispatchObject qds = Recover<DispatchObject>(Ok(Get(db, "QueryDefs")));
        DispatchObject qd = Recover<DispatchObject>(Ok(Get(qds, "Item", "QUnion")));
        SelectRecordsetObject rs = Recover<SelectRecordsetObject>(Ok(Method(qd, "OpenRecordset", 4)));

        Assert.Equal(0, Int32Value(Ok(Get(rs, "AbsolutePosition"))));
        AssertDaoError(Get(rs, "BatchCollisionCount"), 3251, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Get(rs, "Index"), 3251, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Get(rs, "LastModified"), 3251, "DAO.Recordset", errorInfo: false);
        AssertDaoError(Get(rs, "LockEdits"), 3251, "DAO.Recordset", errorInfo: false);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, BytesValue(Ok(Get(rs, "Bookmark"))));
        Assert.True(BoolValue(Ok(Get(rs, "Bookmarkable"))));
        Assert.Equal(0, Int32Value(Ok(Get(rs, "CacheSize"))));
        Assert.Equal("QUnion", TextValue(Ok(Get(rs, "Name"))));
        Assert.Equal(0f, SingleValue(Ok(Get(rs, "PercentPosition"))));
        Assert.Equal(5, Int32Value(Ok(Get(rs, "RecordCount"))));
        Assert.True(BoolValue(Ok(Get(rs, "Restartable"))));
        Assert.False(BoolValue(Ok(Get(rs, "Transactions"))));
        Assert.Equal(4, Int16Value(Ok(Get(rs, "Type"))));
        Assert.False(BoolValue(Ok(Get(rs, "Updatable"))));
        Assert.Equal("", TextValue(Ok(Get(rs, "ValidationRule"))));
        DispatchObject props = Recover<DispatchObject>(Ok(Get(rs, "Properties")));
        Assert.Equal(34, Int16Value(Ok(Get(props, "Count"))));
        Ok(Method(rs, "Close"));
    }

    [Fact]
    public void OpenDatabase_alias_paths_return_canonical_name_and_extended_fails_3043()
    {
        // Replays evidence/r-q2-cursor-dao/boot-alias-path-open-twice calls 3-48.
        string path = CopyFixture(Q2CursorMainAccdb, ".accdb");
        WorkspaceObject ws = _engine.DefaultWorkspace;

        DatabaseObject d0 = Recover<DatabaseObject>(Ok(Method(ws, "OpenDatabase", path, false, false)));
        _databases.Add(d0);
        Assert.Equal(path, TextValue(Ok(Get(d0, "Name"))));

        DatabaseObject dUpper = Recover<DatabaseObject>(Ok(Method(ws, "OpenDatabase", path.ToUpperInvariant(), false, false)));
        _databases.Add(dUpper);
        Assert.Equal(path, TextValue(Ok(Get(dUpper, "Name"))));

        AssertDaoError(Method(ws, "OpenDatabase", @"\\?\" + path, false, false), 3043, "DAO.Workspace", errorInfo: false);

        DatabaseObject dSlashes = Recover<DatabaseObject>(Ok(Method(ws, "OpenDatabase", path.Replace('\\', '/'), false, false)));
        _databases.Add(dSlashes);
        Assert.Equal(path, TextValue(Ok(Get(dSlashes, "Name"))));

        string dotPath = Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
        DatabaseObject dDot = Recover<DatabaseObject>(Ok(Method(ws, "OpenDatabase", dotPath, false, false)));
        _databases.Add(dDot);
        Assert.Equal(path, TextValue(Ok(Get(dDot, "Name"))));
    }

    [Fact]
    public void CreateQueryDef_temporary_top_parameter_forms_fail_3141()
    {
        // Replays evidence/r-sql-13b/top-parameter-forms calls 5, 29, 53, 77, 101.
        DatabaseObject db = Open(Sql13bMainAccdb, readOnly: true);
        string[] sqls =
        [
            "PARAMETERS pn Long; SELECT TOP pn id FROM GA ORDER BY id;",
            "PARAMETERS pn Long; SELECT TOP [pn] id FROM GA ORDER BY id;",
            "PARAMETERS pn Long; SELECT TOP (pn) id FROM GA ORDER BY id;",
            "PARAMETERS pn Double; SELECT TOP pn PERCENT id FROM GA ORDER BY id;",
            "PARAMETERS pn Text ( 5 ); SELECT TOP pn id FROM GA ORDER BY id;",
        ];
        foreach (string sql in sqls)
        {
            AssertDaoError(Method(db, "CreateQueryDef", "", sql), 3141, "DAO.Database", errorInfo: false);
        }
    }

    private string CopyFixture(string sha256, string extension)
    {
        string gz = Path.Combine(RepoRoot(), "evidence", "artifacts", sha256 + ".gz");
        string copy = Path.Combine(Path.GetTempPath(), $"calcdb-w20-{Guid.NewGuid():N}{extension}");
        using (FileStream src = File.OpenRead(gz))
        using (GZipStream unzip = new(src, CompressionMode.Decompress))
        using (FileStream dst = File.Create(copy))
        {
            unzip.CopyTo(dst);
        }
        _files.Add(copy);
        return copy;
    }

    private DatabaseObject Open(string sha256, bool readOnly)
    {
        string path = CopyFixture(sha256, ".accdb");
        Ok(DatabaseObject.Open(_engine.DefaultWorkspace, path, exclusive: false, readOnly: readOnly, out DatabaseObject? db));
        Assert.NotNull(db);
        _databases.Add(db!);
        return db!;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CalcDb.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("CalcDb.slnx not found");
    }

    private nint Arg(object? value)
    {
        NativeVariant* cell = (NativeVariant*)NativeMemory.AllocZeroed((nuint)sizeof(NativeVariant));
        _cells.Add((nint)cell);
        switch (value)
        {
            case null:
                cell->Vt = (ushort)VarType.Empty;
                break;
            case DBNull:
                cell->Vt = (ushort)VarType.Null;
                break;
            case bool b:
                cell->Vt = (ushort)VarType.Bool;
                cell->Int64 = b ? -1 : 0;
                break;
            case short s:
                cell->Vt = (ushort)VarType.I2;
                cell->Int64 = s;
                break;
            case int i:
                cell->Vt = (ushort)VarType.I4;
                cell->Int64 = i;
                break;
            case string text:
                cell->Vt = (ushort)VarType.Bstr;
                cell->Pointer = Marshal.StringToBSTR(text);
                break;
            case ComObject obj:
                nint disp;
                Assert.Equal(HResults.S_OK, obj.QueryInterface(ComIids.IDispatch, &disp));
                cell->Vt = (ushort)VarType.Dispatch;
                cell->Pointer = disp;
                break;
            default:
                throw new NotSupportedException(value.GetType().Name);
        }
        return (nint)cell;
    }

    private CallResult Call(DispatchObject target, string member, InvokeKind kind, params object?[] args)
    {
        nint[] cells = new nint[args.Length];
        for (int i = 0; i < args.Length; i++)
        {
            cells[i] = Arg(args[i]);
        }
        return target.Run(target.Declaration.Member(member, kind), cells);
    }

    private CallResult Get(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyGet, args);
    private CallResult Put(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.PropertyPut, args);
    private CallResult Method(DispatchObject target, string member, params object?[] args) => Call(target, member, InvokeKind.Method, args);

    private CallResult FieldValue(DispatchObject rs, string name)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Get(field, "Value");
    }

    private CallResult PutField(DispatchObject rs, string name, object? value)
    {
        DispatchObject fields = Recover<DispatchObject>(Ok(Get(rs, "Fields")));
        DispatchObject field = Recover<DispatchObject>(Ok(Get(fields, "Item", name)));
        return Put(field, "Value", value);
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

    private static void AssertDaoError(CallResult result, int number, string source, bool errorInfo)
    {
        Assert.NotNull(result.Failure);
        Assert.True(result.Failure!.Kind == FailureKind.Dao, $"Expected Dao {number}, got {result.Failure.Kind}: {result.Failure.Description}");
        Assert.Equal(number, result.Failure.Number);
        Assert.Equal(source, result.Failure.Source);
        Assert.Equal(errorInfo, result.Failure.PublishesErrorInfo);
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

    private static float SingleValue(CallResult result)
    {
        Assert.True(result.HasScalar && (VarType)result.Scalar.Vt == VarType.R4);
        return BitConverter.Int32BitsToSingle((int)result.Scalar.Int64);
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
