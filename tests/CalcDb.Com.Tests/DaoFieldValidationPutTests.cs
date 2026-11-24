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

    [Fact]
    public void Dispatch_invoke_hooks_and_validation_rule_edge_cases_match_records()
    {
        string path = Path.Combine(_dir, "val-edges.accdb");
        DBEngineObject eng = CreateEngine();
        DatabaseObject db = Recover<DatabaseObject>(Method(eng, "CreateDatabase", path, DbLangGeneral, DbVersion120));
        Assert.Null(Method(db, "Execute", "CREATE TABLE T (ID LONG CONSTRAINT PK_T PRIMARY KEY, V LONG, W LONG, S TEXT(40))", DbFailOnError).Failure);
        Assert.Null(Method(db, "Execute", "CREATE INDEX IX_T_S ON T (S)", DbFailOnError).Failure);
        DefinitionCollection tds = Recover<DefinitionCollection>(Get(db, "TableDefs"));
        TableDefObject td = Recover<TableDefObject>(Get(tds, "Item", "T"));
        DefinitionCollection fields = Recover<DefinitionCollection>(Get(td, "Fields"));
        DefinitionFieldObject fv = Recover<DefinitionFieldObject>(Get(fields, "Item", "V"));
        DefinitionFieldObject fs = Recover<DefinitionFieldObject>(Get(fields, "Item", "S"));

        // r-expr-1/mut-validation-rules call 211: constant expression "1/0" is accepted.
        Assert.Null(Put(fv, "ValidationRule", "1/0").Failure);
        Assert.Equal("1/0\0", TextOf(Get(fv, "ValidationRule")));

        // r-expr-1/mut-validation-rules call 236: standalone-unknown function fails DAO 3388 (no IErrorInfo).
        CallResult badFn = Put(fv, "ValidationRule", "CVErr(5)");
        Assert.Equal(unchecked((int)0x800A0D3C), badFn.Failure!.Scode);
        Assert.Equal("DAO.Field", badFn.Failure.Source);
        Assert.Equal("Unknown function 'CVErr' in validation expression or default value on 'T.V'.", badFn.Failure.Description);
        Assert.Equal("jeterr40.chm", badFn.Failure.HelpFile);
        Assert.Equal(5003388, badFn.Failure.HelpContext);
        Assert.False(badFn.Failure.PublishesErrorInfo);

        // ExpressionUse.ValidationRule allows Like on an indexed Text field (unlike Query's NotModelled.IndexedLike).
        Assert.Null(Put(fs, "ValidationRule", "Like 'A*'").Failure);

        // Unrecorded syntax error (unclosed paren) and single-other-column reference refuse by name rather than guessing 3320/3783.
        Assert.Equal(FailureKind.NotImplemented, Put(fv, "ValidationRule", "[V] > (10").Failure!.Kind);
        Assert.Equal(FailureKind.NotImplemented, Put(fv, "ValidationRule", "[W] > 10").Failure!.Kind);

        // IDispatch::Invoke path: VT_NULL -> 3421 with IErrorInfo; VT_I4 -> E_NOTIMPL refusal.
        (int hrNull, ExcepInfo exNull, bool hasEiNull) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Null });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrNull);
        Assert.Equal(unchecked((int)0x800A0D5D), exNull.Scode);
        Assert.True(hasEiNull);
        FreeExcep(exNull);

        (int hrInt, ExcepInfo exInt, _) = InvokePut(fv, "ValidationText", new NativeVariant { Vt = (ushort)VarType.I4, Int64 = 7 });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrInt);
        Assert.Equal(HResults.E_NOTIMPL, exInt.Scode);
        FreeExcep(exInt);

        // Closed Database field: VT_BSTR reports 3420 via Live; VT_NULL refuses by name instead of raising 3421 before Live.
        Assert.Null(Method(db, "Close").Failure);
        nint bstr = Marshal.StringToBSTR("> 1");
        try
        {
            (int hrClosedBstr, ExcepInfo exClosedBstr, _) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Bstr, Pointer = bstr });
            Assert.Equal(HResults.DISP_E_EXCEPTION, hrClosedBstr);
            Assert.Equal(unchecked((int)0x800A0D5C), exClosedBstr.Scode);
            FreeExcep(exClosedBstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }

        (int hrClosedNull, ExcepInfo exClosedNull, _) = InvokePut(fv, "ValidationRule", new NativeVariant { Vt = (ushort)VarType.Null });
        Assert.Equal(HResults.DISP_E_EXCEPTION, hrClosedNull);
        Assert.Equal(HResults.E_NOTIMPL, exClosedNull.Scode);
        FreeExcep(exClosedNull);
    }

    private static (int Hr, ExcepInfo Excep, bool HasErrorInfo) InvokePut(DispatchObject target, string member, NativeVariant value)
    {
        if (NativeRoute.GetErrorInfo(0, out nint stale) == 0 && stale != 0)
        {
            Marshal.Release(stale);
        }
        int propPutId = -3;
        DispParams dp = new()
        {
            Args = (nint)(&value),
            ArgCount = 1,
            NamedArgs = (nint)(&propPutId),
            NamedArgCount = 1,
        };
        NativeVariant result = default;
        ExcepInfo excep = default;
        uint argError = 0;
        int hr = target.Invoke(target.Declaration.Member(member, InvokeKind.PropertyPut).MemberId, (ushort)InvokeKind.PropertyPut, &dp, &result, &excep, &argError);
        bool hasErrorInfo = NativeRoute.GetErrorInfo(0, out nint errorInfo) == 0 && errorInfo != 0;
        if (hasErrorInfo)
        {
            Marshal.Release(errorInfo);
        }
        return (hr, excep, hasErrorInfo);
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
}
