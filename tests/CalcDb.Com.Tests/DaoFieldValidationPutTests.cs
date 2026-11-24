using System.Runtime.InteropServices;

using CalcDb.Com.Com;
using CalcDb.Com.Dao;
using CalcDb.Com.Declarations;
using CalcDb.Com.Host;
using CalcDb.Foundation;
using CalcDb.Values;

namespace CalcDb.Com.Tests;

/// <summary>
/// DAO-level tests for <c>Field.ValidationRule</c> and <c>Field.ValidationText</c> puts on a stored table's field
/// (W-08 task 9dvt; qualified.md <c>r-q37</c>): live getters with trailing NUL on <c>ValidationRule</c>, removal,
/// validation errors (3783, 3320, 3421 with/without <c>IErrorInfo</c>), unchanged file bytes on error, preserved
/// <c>DBEngine.Errors</c> on success, immediate enforcement by subsequent <c>INSERT</c>, and named refusals.
/// </summary>
public sealed unsafe class DaoFieldValidationPutTests : IDisposable
{
    private const string DbLangGeneral = ";LANGID=0x0409;CP=1252;COUNTRY=0";
    private const int DbVersion120 = 128;
    private const int DbFailOnError = 128;

    private readonly string _dir = Directory.CreateTempSubdirectory("calcdb-dao-valrule-").FullName;
    private readonly List<nint> _cells = [];
    private readonly List<nint> _held = [];
    private readonly EngineState _engine = new(new HostBinding(0xDA0_C7E7, Standalone: true, 0, 0, 0, 0, 0));

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
        return ComObject.Recover<T>(p);
    }

    private static string TextOf(CallResult result)
    {
        Assert.True(result.Failure is null, result.Failure?.Description);
        string s = Marshal.PtrToStringBSTR(result.Scalar.Pointer);
        result.ReleaseUntaken();
        return s;
    }

    private (DBEngineObject Engine, DatabaseObject Db, DefinitionFieldObject FieldV, string Path) CreateTableDb()
    {
        string path = Path.Combine(_dir, "val.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W LONG)", DbFailOnError).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DefinitionCollection fs = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fs, "Item", "V"));
        return (eng, db, fv, path);
    }

    [Fact]
    public void Put_rule_and_text_roundtrip_enforces_on_insert_and_removes_cleanly()
    {
        (_, DatabaseObject db, DefinitionFieldObject fv, _) = CreateTableDb();

        Assert.Null(Put(fv, "ValidationRule", "> 10").Failure);
        Assert.Null(Put(fv, "ValidationText", "must exceed 10").Failure);
        Assert.Equal("> 10\0", TextOf(Get(fv, "ValidationRule")));
        Assert.Equal("must exceed 10", TextOf(Get(fv, "ValidationText")));

        CallResult rejected = Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (1, 5, 0)", DbFailOnError);
        Assert.NotNull(rejected.Failure);
        Assert.Equal(3316, rejected.Failure!.Scode & 0xFFFF);
        Assert.Equal("must exceed 10", rejected.Failure.Description);

        Assert.Null(Put(fv, "ValidationRule", "").Failure);
        Assert.Null(Put(fv, "ValidationText", "").Failure);
        Assert.Equal("", TextOf(Get(fv, "ValidationRule")));
        Assert.Equal("", TextOf(Get(fv, "ValidationText")));
        Assert.Null(Method(db, "Execute", "INSERT INTO T (ID, V, W) VALUES (1, 5, 0)", DbFailOnError).Failure);
        Assert.Null(Method(db, "Close").Failure);
    }

    [Fact]
    public void Validation_errors_match_ace_codes_keep_file_unchanged_and_preserve_errors_on_success()
    {
        (DBEngineObject eng, DatabaseObject db, DefinitionFieldObject fv, string path) = CreateTableDb();
        Assert.Null(Method(db, "Close").Failure);
        byte[] before = File.ReadAllBytes(path);

        DatabaseObject db2 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        TableDefObject td = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db2, "TableDefs")), "Item", "T"));
        DefinitionFieldObject fv2 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(td, "Fields")), "Item", "V"));

        CallResult multi = Put(fv2, "ValidationRule", "[V] < [W]");
        Assert.Equal(unchecked((int)0x800A0EC7), multi.Failure!.Scode);
        Assert.Equal("DAO.Field", multi.Failure.Source);
        Assert.Null(multi.Failure.HelpFile);
        Assert.Equal(-1, multi.Failure.HelpContext);
        Assert.False(multi.Failure.PublishesErrorInfo);

        CallResult syn = Put(fv2, "ValidationRule", ">");
        Assert.Equal(unchecked((int)0x800A0CF8), syn.Failure!.Scode);
        Assert.Equal("DAO.Field", syn.Failure.Source);
        Assert.Equal("jeterr40.chm", syn.Failure.HelpFile);
        Assert.Equal(5003320, syn.Failure.HelpContext);
        Assert.False(syn.Failure.PublishesErrorInfo);

        CallResult nullRule = Put(fv2, "ValidationRule", DBNull.Value);
        Assert.Equal(unchecked((int)0x800A0D5D), nullRule.Failure!.Scode);
        Assert.True(nullRule.Failure.PublishesErrorInfo);

        CallResult nullText = Put(fv2, "ValidationText", DBNull.Value);
        Assert.Equal(unchecked((int)0x800A0D5D), nullText.Failure!.Scode);
        Assert.True(nullText.Failure.PublishesErrorInfo);

        Assert.Null(Method(db2, "Close").Failure);
        Assert.Equal(before, File.ReadAllBytes(path));

        // Success after an error preserves DBEngine.Errors; replacing a 2-page chained blob refuses by name.
        DatabaseObject db3 = Recover<DatabaseObject>(Method(eng, "OpenDatabase", path, false, false));
        TableDefObject td3 = Recover<TableDefObject>(Get(Recover<DefinitionCollection>(Get(db3, "TableDefs")), "Item", "T"));
        DefinitionFieldObject fv3 = Recover<DefinitionFieldObject>(Get(Recover<DefinitionCollection>(Get(td3, "Fields")), "Item", "V"));
        Assert.Equal(unchecked((int)0x800A0CF8), Put(fv3, "ValidationRule", ">").Failure!.Scode);
        string twoPage = "<> '" + new string('a', 1040) + "' And <> '" + new string('b', 1040) + "'";
        Assert.Null(Put(fv3, "ValidationRule", twoPage).Failure);
        Assert.Single(eng.Engine.Errors);
        Assert.Equal(3320, eng.Engine.Errors[0].Number);

        CallResult chainedReplace = Put(fv3, "ValidationText", "x");
        Assert.Equal(FailureKind.NotImplemented, chainedReplace.Failure!.Kind);
        Assert.Null(Method(db3, "Close").Failure);
    }
}
